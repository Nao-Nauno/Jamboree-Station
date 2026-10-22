// SPDX-FileCopyrightText: 2026 nao-nauno <desk.nao@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Jamboree.Telecomms;
using Content.Shared.Radio;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Jamboree;

[TestFixture]
public sealed class NtslTelecomTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: NtslTestTelecomServer
  parent: TelecomServerFilled
  components:
  - type: ApcPowerReceiver
    needsPower: false
";

    private const string Script = @"
def process_signal(sig) {
    words = explode(sig.content, "" "");
    if (sig.source == ""Bob"") { sig.pass = 0; }
    if (words[1] == ""/block"") { return null; }
    if (words[1] == ""/med"") {
        sig.content = substr(sig.content, 6);
        sig.freq = channels.medical;
    }
    if (words[1] == ""/loud"") {
        sig.filters = list(filter_types.loud, ""italics"");
    }
    if (words[1] == ""/all"") {
        sig.pass = 0;
        sig.content = implode(words - words[1], "" "");
        freqs = list(1459, 1447, 1359, 1357, 1355, 1353, 1351, 1349, 1347);
        sent = 0;
        for (i = 1; i <= freqs.len; i += 1) {
            sig.freq = freqs[i];
            sent += broadcast(sig);
        }
        mem(""sent"", sent);
    }
    sig.source = upper(sig.source);
    return sig;
}
";

    [Test]
    public async Task ScriptsRewriteRerouteAndBlock()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var ntsl = entMan.System<NtslTelecomSystem>();

        var speaker = EntityUid.Invalid;
        var telecom = EntityUid.Invalid;
        await server.WaitAssertion(() =>
        {
            telecom = entMan.SpawnEntity("NtslTestTelecomServer", testMap.GridCoords);
            speaker = entMan.SpawnEntity(null, testMap.GridCoords);

            Assert.That(ntsl.TryUpload(telecom, Script, speaker, out var error), Is.True, error);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var common = protoMan.Index<RadioChannelPrototype>("Common");

            bool Send(string text, string sender, out string message, out RadioChannelPrototype channel,
                out string name, out NtslFilters filters)
            {
                message = text;
                channel = common;
                name = sender;
                string job = null;
                return ntsl.ProcessRadio(speaker, ref message, ref channel, ref name, ref job, out filters);
            }

            var muted = Send("hello", "Bob", out _, out _, out _, out _);
            var blocked = Send("/block this", "Alice", out _, out _, out _, out _);
            var rerouted = Send("/med help me", "Alice", out var medMessage, out var medChannel, out var medName, out _);
            var loud = Send("/loud HEY", "Alice", out _, out _, out _, out var loudFilters);
            var all = Send("/all everyone listen", "Alice", out _, out _, out _, out _);

            Assert.Multiple(() =>
            {
                Assert.That(muted, Is.False);
                Assert.That(blocked, Is.False);
                Assert.That(rerouted, Is.True);
                Assert.That(medMessage, Is.EqualTo("help me"));
                Assert.That(medChannel.ID, Is.EqualTo("Medical"));
                Assert.That(medName, Is.EqualTo("ALICE"));
                Assert.That(loud, Is.True);
                Assert.That(loudFilters, Is.EqualTo(NtslFilters.Loud | NtslFilters.Italics));
                Assert.That(all, Is.False);
                Assert.That(entMan.GetComponent<NtslServerComponent>(telecom).Memory["sent"], Is.EqualTo(8.0));
                Assert.That(NtslTelecomSystem.ApplyFilters("hi", NtslFilters.Loud | NtslFilters.Italics), Is.EqualTo($"[font size={NtslTelecomSystem.LoudFontSize}][italic]hi[/italic][/font]"));
            });
        });

        await pair.RunTicksSync(5);

        await pair.CleanReturnAsync();
    }
}
