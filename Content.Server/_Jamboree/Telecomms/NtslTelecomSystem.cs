// SPDX-FileCopyrightText: 2026 nao-nauno <desk.nao@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using Content.Server._Jamboree.Ntsl;
using Content.Server.Administration.Logs;
using Content.Server.GameTicking;
using Content.Server.Power.Components;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Jamboree.Telecomms;

/// <summary>
/// Compiles NTSL scripts onto telecom servers and runs them on the radio messages those servers operate.
/// </summary>
public sealed class NtslTelecomSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly RadioSystem _radio = default!;

    public const string EntryFunction = "process_signal";
    public const int MaxBroadcastsPerRun = 5;
    public const int MaxMemoryKeys = 500;
    public const int MaxLogEntries = 20;
    public const int MaxNameLength = 32;
    public const int MaxLoggedCodeLength = 2000;
    public static readonly TimeSpan UploadCooldown = TimeSpan.FromSeconds(2);

    private readonly Queue<PendingBroadcast> _broadcasts = new();
    private readonly List<Entity<NtslServerComponent, EncryptionKeyHolderComponent>> _servers = new();
    private int _broadcastsThisRun;
    private string? _broadcastName;
    private bool _broadcasting;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NtslServerComponent, TransformSpeakerNameEvent>(OnTransformSpeakerName);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Broadcasts wait for the next tick to let the queue clean itself.
        while (_broadcasts.TryDequeue(out var broadcast))
        {
            if (TerminatingOrDeleted(broadcast.Server))
                continue;

            _broadcasting = true;
            _broadcastName = broadcast.Source;
            try
            {
                _radio.SendRadioMessage(broadcast.Server, broadcast.Content, broadcast.Channel, broadcast.Server);
            }
            finally
            {
                _broadcasting = false;
                _broadcastName = null;
            }
        }
    }

    private void OnTransformSpeakerName(Entity<NtslServerComponent> ent, ref TransformSpeakerNameEvent args)
    {
        if (_broadcasting && _broadcastName != null)
            args.VoiceName = _broadcastName;
    }

    public bool TryUpload(EntityUid server, string code, EntityUid user, [NotNullWhen(false)] out string? error)
    {
        var comp = EnsureComp<NtslServerComponent>(server);
        if (_timing.CurTime < comp.NextUpload)
        {
            error = Loc.GetString("ntsl-error-upload-cooldown");
            return false;
        }

        comp.NextUpload = _timing.CurTime + UploadCooldown;

        NtslInterpreter interpreter;
        try
        {
            interpreter = NtslInterpreter.Compile(code);
            Bind(server, interpreter);
            _broadcastsThisRun = 0;
            interpreter.Run();
        }
        catch (NtslException e)
        {
            error = FormatError(e);
            return false;
        }

        comp.Code = code;
        comp.Interpreter = interpreter;
        comp.Log.Clear();

        var logged = code.Length > MaxLoggedCodeLength ? code[..MaxLoggedCodeLength] : code;
        _adminLogger.Add(LogType.ItemConfigure, LogImpact.Medium,
            $"{ToPrettyString(user):user} uploaded an NTSL script ({code.Length} characters) to {ToPrettyString(server):server}:\n{logged}");

        error = null;
        return true;
    }

    public void Clear(EntityUid server, EntityUid? user)
    {
        if (!TryComp<NtslServerComponent>(server, out var comp))
            return;

        comp.Code = string.Empty;
        comp.Interpreter = null;
        comp.Memory.Clear();
        comp.Log.Clear();

        if (user != null)
        {
            _adminLogger.Add(LogType.ItemConfigure, LogImpact.Medium,
                $"{ToPrettyString(user.Value):user} cleared the NTSL script on {ToPrettyString(server):server}");
        }
    }

    public void SetEnabled(EntityUid server, bool enabled, EntityUid user)
    {
        var comp = EnsureComp<NtslServerComponent>(server);
        comp.Enabled = enabled;

        _adminLogger.Add(LogType.ItemConfigure, LogImpact.Medium, $"{ToPrettyString(user):user} turned NTSL scripts {(enabled ? "on" : "off")} on {ToPrettyString(server):server}");
    }

    /// <summary>
    /// Runs the scripts of every powered telecom server that operates this channel.
    /// Returns false if a script blocked the message.
    /// </summary>
    public bool ProcessRadio(EntityUid radioSource, ref string message, ref RadioChannelPrototype channel, ref string name, ref string? job)
    {
        if (_broadcasting)
            return true;

        var mapId = Transform(radioSource).MapID;
        _servers.Clear();

        var query = EntityQueryEnumerator<NtslServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var script, out var keys, out var power, out var xform))
        {
            if (script.Enabled
                && script.Code.Length > 0
                && power.Powered
                && xform.MapID == mapId
                && keys.Channels.Contains(channel.ID))
            {
                _servers.Add((uid, script, keys));
            }
        }

        if (_servers.Count == 0)
            return true;

        foreach (var server in _servers)
        {
            // TODO
            var interpreter = server.Comp1.Interpreter;
            if (interpreter == null || !interpreter.HasFunction(EntryFunction))
                continue;

            var signal = MakeSignal(message, channel.Frequency, name, job ?? string.Empty);
            signal.Set("channel", channel.LocalizedName);

            object? result;
            try
            {
                _broadcastsThisRun = 0;
                result = interpreter.Call(EntryFunction, signal);
            }
            catch (NtslException e)
            {
                AddLog(server.Comp1, FormatError(e));
                continue;
            }

            if (result is not NtslList output)
                continue;

            if (!NtslInterpreter.IsTrue(output.Get("pass")))
                return false;

            message = CleanText(NtslInterpreter.ToText(output.Get("content")), NtslInterpreter.MaxStringLength, "*beep*");
            name = CleanText(NtslInterpreter.ToText(output.Get("source")), MaxNameLength, name);
            job = CleanText(NtslInterpreter.ToText(output.Get("job")), MaxNameLength, string.Empty);

            if (output.Get("freq") is double frequency
                && frequency != channel.Frequency
                && TryGetChannel(server.Comp2, frequency, out var rerouted))
            {
                channel = rerouted;
            }
        }

        return true;
    }

    public string FormatError(NtslException e)
    {
        return Loc.GetString("ntsl-error-at-line", ("line", e.Line), ("error", Loc.GetString(e.Message, e.Args)));
    }

    private void Bind(EntityUid server, NtslInterpreter interpreter)
    {
        var channels = new NtslList();
        foreach (var proto in _prototype.EnumeratePrototypes<RadioChannelPrototype>())
        {
            channels.Set(proto.ID.ToLowerInvariant(), (double) proto.Frequency);
        }

        interpreter.Define("channels", channels);

        interpreter.Register("signal", Signal);
        interpreter.Register("broadcast", (args, line) => Broadcast(server, args, line));
        interpreter.Register("mem", (args, line) => Mem(server, args, line));
        interpreter.Register("clearmem", (_, _) => ClearMem(server));
        interpreter.Register("time", (_, _) => _ticker.RoundDuration().TotalSeconds);
    }

    private double CommonFrequency => _prototype.Index(SharedChatSystem.CommonChannel).Frequency;

    private static NtslList MakeSignal(string content, double frequency, string source, string job)
    {
        var signal = new NtslList();
        signal.Set("content", content);
        signal.Set("freq", frequency);
        signal.Set("source", source);
        signal.Set("job", job);
        signal.Set("pass", 1.0);
        return signal;
    }

    private object? Signal(List<object?> args, int line)
    {
        var frequency = args.Count > 1 && args[1] != null ? NtslInterpreter.ToNumber(args[1], line) : CommonFrequency;
        return MakeSignal(
            NtslInterpreter.ToText(args.Count > 0 ? args[0] : null),
            frequency,
            NtslInterpreter.ToText(args.Count > 2 ? args[2] : null),
            NtslInterpreter.ToText(args.Count > 3 ? args[3] : null));
    }

    private object? Broadcast(EntityUid server, List<object?> args, int line)
    {
        if (args.Count == 0 || args[0] is not NtslList signal)
            throw new NtslException("ntsl-error-not-a-signal", line);

        if (++_broadcastsThisRun > MaxBroadcastsPerRun)
            throw new NtslException("ntsl-error-too-many-broadcasts", line, ("max", MaxBroadcastsPerRun));

        if (!TryComp<EncryptionKeyHolderComponent>(server, out var keys))
            return 0.0;

        var frequency = signal.Get("freq") is double number ? number : CommonFrequency;
        if (!TryGetChannel(keys, frequency, out var channel))
            throw new NtslException("ntsl-error-channel-not-on-server", line, ("freq", frequency));

        var content = CleanText(NtslInterpreter.ToText(signal.Get("content")), NtslInterpreter.MaxStringLength, "*beep*");
        var source = CleanText(NtslInterpreter.ToText(signal.Get("source")), MaxNameLength, Name(server));
        _broadcasts.Enqueue(new PendingBroadcast(server, content, source, channel));
        return 1.0;
    }

    private object? Mem(EntityUid server, List<object?> args, int line)
    {
        if (args.Count == 0 || args[0] is not string key)
            throw new NtslException("ntsl-error-bad-key", line);

        if (!TryComp<NtslServerComponent>(server, out var comp))
            return null;

        if (args.Count == 1)
            return comp.Memory.GetValueOrDefault(key);

        if (args[1] == null)
        {
            comp.Memory.Remove(key);
            return 1.0;
        }

        if (!comp.Memory.ContainsKey(key) && comp.Memory.Count >= MaxMemoryKeys)
            return 0.0;

        comp.Memory[key] = args[1];
        return 1.0;
    }

    private object? ClearMem(EntityUid server)
    {
        if (TryComp<NtslServerComponent>(server, out var comp))
            comp.Memory.Clear();

        return null;
    }

    private bool TryGetChannel(
        EncryptionKeyHolderComponent keys,
        double frequency,
        [NotNullWhen(true)] out RadioChannelPrototype? channel)
    {
        foreach (var id in keys.Channels)
        {
            if (_prototype.TryIndex<RadioChannelPrototype>(id, out var proto) && proto.Frequency == frequency)
            {
                channel = proto;
                return true;
            }
        }

        channel = null;
        return false;
    }

    private void AddLog(NtslServerComponent comp, string text)
    {
        comp.Log.Add($"[{_ticker.RoundDuration().ToString(@"hh\:mm\:ss")}] {text}");
        if (comp.Log.Count > MaxLogEntries)
            comp.Log.RemoveAt(0);
    }

    private static string CleanText(string text, int maxLength, string fallback)
    {
        var clean = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        if (clean.Length > maxLength)
            clean = clean[..maxLength];

        return clean.Length == 0 ? fallback : clean;
    }

    private readonly record struct PendingBroadcast(
        EntityUid Server,
        string Content,
        string Source,
        RadioChannelPrototype Channel);
}
