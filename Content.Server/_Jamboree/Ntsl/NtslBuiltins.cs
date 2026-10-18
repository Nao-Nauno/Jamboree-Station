// SPDX-FileCopyrightText: 2026 nao-nauno <desk.nao@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Linq;

namespace Content.Server._Jamboree.Ntsl;

/// <summary>
/// The functions every NTSL script can use, whatever device runs it.
/// </summary>
public sealed partial class NtslInterpreter
{
    private readonly Random _random = new();

    private void RegisterCoreBuiltins()
    {
        _builtins.Declare("true", 1.0);
        _builtins.Declare("TRUE", 1.0);
        _builtins.Declare("false", 0.0);
        _builtins.Declare("FALSE", 0.0);
        _builtins.Declare("null", null);
        _builtins.Declare("PI", Math.PI);

        Register("length", Length);
        Register("lower", (args, _) => ToText(Arg(args, 0)).ToLowerInvariant());
        Register("upper", (args, _) => ToText(Arg(args, 0)).ToUpperInvariant());
        Register("tostring", (args, _) => ToText(Arg(args, 0)));
        Register("tonum", ToNum);
        Register("find", Find);
        Register("substr", Substr);
        Register("replace", Replace);
        Register("explode", Explode);
        Register("implode", Implode);

        Register("abs", (args, line) => Math.Abs(ToNumber(Arg(args, 0), line)));
        Register("round", (args, line) => Math.Round(ToNumber(Arg(args, 0), line), MidpointRounding.AwayFromZero));
        Register("floor", (args, line) => Math.Floor(ToNumber(Arg(args, 0), line)));
        Register("ceil", (args, line) => Math.Ceiling(ToNumber(Arg(args, 0), line)));
        Register("sqrt", (args, line) => Math.Sqrt(ToNumber(Arg(args, 0), line)));
        Register("min", (args, line) => Extreme(args, line, Math.Min));
        Register("max", (args, line) => Extreme(args, line, Math.Max));

        Register("prob", (args, line) => _random.NextDouble() * 100 < ToNumber(Arg(args, 0), line) ? 1.0 : 0.0);
        Register("rand", Rand);
        Register("pick", Pick);
    }

    private static object? Arg(List<object?> args, int index)
    {
        return index < args.Count ? args[index] : null;
    }

    private static object? Length(List<object?> args, int line)
    {
        return Arg(args, 0) switch
        {
            string text => (double) text.Length,
            NtslList list => (double) list.Items.Count,
            _ => 0.0,
        };
    }

    private static object? ToNum(List<object?> args, int line)
    {
        var text = ToText(Arg(args, 0));
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static object? Find(List<object?> args, int line)
    {
        var needle = Arg(args, 1);
        if (Arg(args, 0) is NtslList list)
            return (double) (list.Items.FindIndex(item => AreEqual(item, needle)) + 1);

        var haystack = ToText(Arg(args, 0));
        return (double) (haystack.IndexOf(ToText(needle), StringComparison.OrdinalIgnoreCase) + 1);
    }

    private static object? Substr(List<object?> args, int line)
    {
        var text = ToText(Arg(args, 0));
        var start = args.Count > 1 ? (int) ToNumber(args[1], line) : 1;
        var end = args.Count > 2 ? (int) ToNumber(args[2], line) : 0;

        start = Math.Clamp(start, 1, text.Length + 1);
        if (end <= 0 || end > text.Length + 1)
            end = text.Length + 1;

        return end <= start ? string.Empty : text[(start - 1)..(end - 1)];
    }

    private static object? Replace(List<object?> args, int line)
    {
        var text = ToText(Arg(args, 0));
        var find = ToText(Arg(args, 1));
        if (find.Length == 0)
            return text;

        return text.Replace(find, ToText(Arg(args, 2)), StringComparison.OrdinalIgnoreCase);
    }

    private static object? Explode(List<object?> args, int line)
    {
        var text = ToText(Arg(args, 0));
        var separator = ToText(Arg(args, 1));
        var parts = separator.Length == 0
            ? text.Select(c => c.ToString())
            : text.Split(separator);

        var list = new NtslList();
        list.Items.AddRange(parts.Take(MaxListLength));
        return list;
    }

    private static object? Implode(List<object?> args, int line)
    {
        if (Arg(args, 0) is not NtslList list)
            throw new NtslException("ntsl-error-not-a-list", line);

        return string.Join(ToText(Arg(args, 1)), list.Items.Select(ToText));
    }

    private static object? Extreme(List<object?> args, int line, Func<double, double, double> choose)
    {
        var values = args.Count == 1 && args[0] is NtslList list ? list.Items : args;
        if (values.Count == 0)
            return null;

        var result = ToNumber(values[0], line);
        for (var i = 1; i < values.Count; i++)
        {
            result = choose(result, ToNumber(values[i], line));
        }

        return result;
    }

    private object? Rand(List<object?> args, int line)
    {
        if (args.Count < 2)
            return _random.NextDouble();

        var low = (long) ToNumber(args[0], line);
        var high = (long) ToNumber(args[1], line);
        if (high < low)
            (low, high) = (high, low);

        return (double) _random.NextInt64(low, high + 1);
    }

    private object? Pick(List<object?> args, int line)
    {
        var pool = new List<object?>();
        foreach (var arg in args)
        {
            if (arg is NtslList list)
                pool.AddRange(list.Items);
            else
                pool.Add(arg);
        }

        return pool.Count == 0 ? null : pool[_random.Next(pool.Count)];
    }
}
