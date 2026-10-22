// SPDX-FileCopyrightText: 2026 nao-nauno <desk.nao@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Jamboree.Ntsl;
using NUnit.Framework;

namespace Content.Tests.Server._Jamboree.Ntsl;

[TestFixture]
public sealed class NtslInterpreterTest
{
    private static string Run(string code, params object[] args)
    {
        var interpreter = NtslInterpreter.Compile(code);
        interpreter.Run();
        return NtslInterpreter.ToText(interpreter.Call("get", args));
    }

    private static string Error(string code)
    {
        try
        {
            Run(code);
        }
        catch (NtslException e)
        {
            return e.Message;
        }

        return "no error";
    }

    private static NtslList Signal(string content, string source)
    {
        var signal = new NtslList();
        signal.Set("content", content);
        signal.Set("source", source);
        signal.Set("pass", 1.0);
        return signal;
    }

    [Test]
    [TestCase("return 1 + 2 * 3 - 4 / 2 ^ 2;", "6")]
    [TestCase("return \"a\" + 1 + \"b\";", "a1b")]
    [TestCase("return tostring(!0) + -(-3) + !\"\" + !\"a\";", "1310")]
    [TestCase("return 7 % 3 == 1 && 2 >= 2 || 0;", "1")]
    [TestCase("return substr(\"hello\", 2, 4);", "el")]
    [TestCase("return find(\"Hello World\", \"world\");", "7")]
    [TestCase("return replace(\"a-b-c\", \"-\", \"+\");", "a+b+c")]
    [TestCase("return explode(\"a b c\", \" \").len;", "3")]
    [TestCase("return implode(list(1, 2, 3) - 2, \",\");", "1,3")]
    [TestCase("return tostring(min(3, 1, 2)) + max(list(4, 9, 2));", "19")]
    [TestCase("return upper(\"radio\") + lower(\"CHECK\");", "RADIOcheck")]
    [TestCase("return tonum(\"12.5\") * 2;", "25")]
    public void Expressions(string body, string expected)
    {
        Assert.That(Run($"def get() {{ {body} }}"), Is.EqualTo(expected));
    }

    [Test]
    public void ControlFlow()
    {
        const string code = """
            def get() {
                total = 0;
                for (i = 1; i <= 50; i += 1) {
                    if (i % 2 == 0) { continue; }
                    if (i > 9) { break; }
                    total += i;
                }
                return total;
            }
            """;

        Assert.That(Run(code), Is.EqualTo("25"));
    }

    [Test]
    public void IfChains()
    {
        const string code = """
            def get(x) {
                if (x == 1) { return "a"; }
                elseif (x == 2) { return "b"; }
                else if (x == 3) { return "c"; }
                else { return "d"; }
            }
            """;

        Assert.That(Run(code, 1.0) + Run(code, 2.0) + Run(code, 3.0) + Run(code, 4.0), Is.EqualTo("abcd"));
    }

    [Test]
    public void FunctionsAndClosures()
    {
        Assert.That(Run("def fib(n) { if (n < 2) { return n; } return fib(n - 1) + fib(n - 2); } def get() { return fib(10); }"),
            Is.EqualTo("55"));
        Assert.That(Run("def make() { n = 5; def add(x) { return x + n; } return add; } def get() { f = make(); return f(2); }"),
            Is.EqualTo("7"));
    }

    [Test]
    public void Lists()
    {
        Assert.That(Run("def get() { l = list(1, 2, \"k\" = \"v\"); l += 4; l[1] = 9; return tostring(l[1]) + l[\"k\"] + l.len + l[4]; }"),
            Is.EqualTo("9v44"));
    }

    [Test]
    public void GlobalsPersistBetweenCalls()
    {
        var interpreter = NtslInterpreter.Compile("count = 0; def get() { count += 1; return count; }");
        interpreter.Run();
        interpreter.Call("get");
        interpreter.Call("get");

        Assert.That(NtslInterpreter.ToText(interpreter.Call("get")), Is.EqualTo("3"));
    }

    [Test]
    public void ProcessSignal()
    {
        const string code = """
            def process_signal(sig) {
                if (sig.source == "Bob") { sig.pass = 0; }
                words = explode(sig.content, " ");
                if (words[1] == "/anon") {
                    sig.source = "Anonymous";
                    sig.content = substr(sig.content, 7);
                }
                return sig;
            }
            """;

        var interpreter = NtslInterpreter.Compile(code);
        interpreter.Run();

        var muted = (NtslList) interpreter.Call("process_signal", Signal("hi", "Bob"))!;
        var anonymous = (NtslList) interpreter.Call("process_signal", Signal("/anon hello all", "Alice"))!;

        Assert.Multiple(() =>
        {
            Assert.That(muted.Get("pass"), Is.EqualTo(0.0));
            Assert.That(anonymous.Get("source"), Is.EqualTo("Anonymous"));
            Assert.That(anonymous.Get("content"), Is.EqualTo("hello all"));
        });
    }

    [Test]
    public void Limits()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Error("def get() { while (1) { } }"), Is.EqualTo("ntsl-error-too-many-loops"));
            Assert.That(Error("def get() { for (i = 0; i < 100; i += 1) { for (j = 0; j < 100; j += 1) { x = 1; } } }"),
                Is.EqualTo("ntsl-error-too-many-statements"));
            Assert.That(Error("def f(n) { return f(n + 1); } def get() { return f(0); }"),
                Is.EqualTo("ntsl-error-too-much-recursion"));
            Assert.That(Error("def get() { l = list(); for (i = 0; i < 100; i += 1) { l += list(1, 2, 3); } }"),
                Is.EqualTo("ntsl-error-list-too-long"));
            Assert.That(Error(new string(' ', NtslInterpreter.MaxCodeLength + 1)), Is.EqualTo("ntsl-error-code-too-long"));
            Assert.That(Error(new string('(', 500) + "1" + new string(')', 500) + ";"), Is.EqualTo("ntsl-error-too-deep"));
        });

        Assert.That(Run("def get() { s = \"x\"; for (i = 0; i < 20; i += 1) { s += s; } return length(s); }"),
            Is.EqualTo(NtslInterpreter.MaxStringLength.ToString()));
    }

    [Test]
    public void Errors()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Error("def get() { return 1 / 0; }"), Is.EqualTo("ntsl-error-divide-by-zero"));
            Assert.That(Error("def get() { return nope; }"), Is.EqualTo("ntsl-error-unknown-name"));
            Assert.That(Error("def get() { l = list(1); l[5] = 2; }"), Is.EqualTo("ntsl-error-index-out-of-range"));
            Assert.That(Error("x = ;"), Is.EqualTo("ntsl-error-expected-expression"));
            Assert.That(Error("if (a) { b = 1;"), Is.EqualTo("ntsl-error-unexpected-end"));
            Assert.That(Error("1 = 2;"), Is.EqualTo("ntsl-error-invalid-assignment"));
            Assert.That(Error("x = \"open;"), Is.EqualTo("ntsl-error-unclosed-string"));
            Assert.That(Error("x = 1 # 2;"), Is.EqualTo("ntsl-error-unexpected-character"));
        });
    }

    [Test]
    public void ShortCircuit()
    {
        Assert.That(Run("def boom() { return 1 / 0; } def get() { return 0 && boom(); }"), Is.EqualTo("0"));
    }

    [Test]
    [TestCase("return l[5];", "")]
    [TestCase("return l[0];", "")]
    [TestCase("return \"abc\"[9];", "")]
    [TestCase("m = list(); m -= m[1]; return m.len;", "0")]
    [TestCase("return explode(\"/calc\", \" \")[2] == null;", "1")]
    public void MissingIndexIsNull(string body, string expected)
    {
        Assert.That(Run($"def get() {{ l = list(1); {body} }}"), Is.EqualTo(expected));
    }
}
