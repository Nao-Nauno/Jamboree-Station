// SPDX-FileCopyrightText: 2026 nao-nauno <desk.nao@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Jamboree.Telecomms;
using Content.Shared.Radio.Components;

namespace Content.Server._Jamboree.Telecomms;

/// <summary>
/// Handles the traffic control console UI: choosing a telecom server and uploading, clearing or toggling its script.
/// </summary>
public sealed class NtslConsoleSystem : EntitySystem
{
    [Dependency] private readonly NtslTelecomSystem _ntsl = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NtslConsoleComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NtslConsoleComponent, NtslConsoleSelectServerMessage>(OnSelectServer);
        SubscribeLocalEvent<NtslConsoleComponent, NtslConsoleUploadMessage>(OnUpload);
        SubscribeLocalEvent<NtslConsoleComponent, NtslConsoleClearMessage>(OnClear);
        SubscribeLocalEvent<NtslConsoleComponent, NtslConsoleSetEnabledMessage>(OnSetEnabled);
        SubscribeLocalEvent<NtslConsoleComponent, NtslConsoleRefreshMessage>(OnRefresh);
    }

    private void OnOpened(Entity<NtslConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (NtslConsoleUiKey.Key.Equals(args.UiKey))
            UpdateUi(ent);
    }

    private void OnSelectServer(Entity<NtslConsoleComponent> ent, ref NtslConsoleSelectServerMessage args)
    {
        if (!TryGetEntity(args.Server, out var server) || !IsValidServer(ent, server.Value))
            return;

        ent.Comp.SelectedServer = server;
        ent.Comp.Output = null;
        UpdateUi(ent);
    }

    private void OnUpload(Entity<NtslConsoleComponent> ent, ref NtslConsoleUploadMessage args)
    {
        if (!TryGetSelectedServer(ent, out var server))
            return;

        ent.Comp.Output = _ntsl.TryUpload(server, args.Code, args.Actor, out var error)
            ? Loc.GetString("ntsl-console-upload-success")
            : error;

        UpdateUi(ent);
    }

    private void OnClear(Entity<NtslConsoleComponent> ent, ref NtslConsoleClearMessage args)
    {
        if (!TryGetSelectedServer(ent, out var server))
            return;

        _ntsl.Clear(server, args.Actor);
        ent.Comp.Output = Loc.GetString("ntsl-console-cleared");
        UpdateUi(ent);
    }

    private void OnSetEnabled(Entity<NtslConsoleComponent> ent, ref NtslConsoleSetEnabledMessage args)
    {
        if (!TryGetSelectedServer(ent, out var server))
            return;

        _ntsl.SetEnabled(server, args.Enabled, args.Actor);
        UpdateUi(ent);
    }

    private void OnRefresh(Entity<NtslConsoleComponent> ent, ref NtslConsoleRefreshMessage args)
    {
        UpdateUi(ent);
    }

    private bool TryGetSelectedServer(Entity<NtslConsoleComponent> console, out EntityUid server)
    {
        server = console.Comp.SelectedServer ?? EntityUid.Invalid;
        return console.Comp.SelectedServer != null && IsValidServer(console, server);
    }

    private bool IsValidServer(EntityUid console, EntityUid server)
    {
        return !TerminatingOrDeleted(server)
               && HasComp<TelecomServerComponent>(server)
               && Transform(server).MapID == Transform(console).MapID;
    }

    private void UpdateUi(Entity<NtslConsoleComponent> console)
    {
        var mapId = Transform(console).MapID;
        var servers = new List<NtslConsoleServerEntry>();
        var query = EntityQueryEnumerator<TelecomServerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapID == mapId)
                servers.Add(new NtslConsoleServerEntry(GetNetEntity(uid), GetLabel(uid)));
        }

        servers.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.Ordinal));

        if (console.Comp.SelectedServer is { } previous && !IsValidServer(console, previous))
            console.Comp.SelectedServer = null;

        NetEntity? selected = null;
        var enabled = true;
        var code = string.Empty;
        var log = new List<string>();

        if (console.Comp.SelectedServer is { } server)
        {
            selected = GetNetEntity(server);
            if (TryComp<NtslServerComponent>(server, out var script))
            {
                enabled = script.Enabled;
                code = script.Code;
                log.AddRange(script.Log);
            }
        }

        var state = new NtslConsoleBoundUserInterfaceState(servers, selected, enabled, code, log, console.Comp.Output);
        _ui.SetUiState(console.Owner, NtslConsoleUiKey.Key, state);
    }

    private string GetLabel(EntityUid server)
    {
        return Loc.GetString("ntsl-console-server-label", ("name", Name(server)), ("id", GetNetEntity(server).Id));
    }
}
