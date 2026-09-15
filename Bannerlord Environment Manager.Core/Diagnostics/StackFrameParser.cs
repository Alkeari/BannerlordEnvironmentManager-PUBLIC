using System.Globalization;
using System.Text.RegularExpressions;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public sealed record CrashFrame(
    string Text,
    string QualifiedName,
    string DeclaringTypeFullName,
    string MethodName,
    string OriginalMethodName,
    string Parameters,
    int Depth,
    bool IsHarmonyReplacement,
    bool IsReflectionBoundary,
    string? SourceFile = null,
    int? SourceLine = null)
{
    public bool HasSourceInfo => SourceFile is not null;
}

public static partial class StackFrameParser
{
    [GeneratedRegex(@"^\s*at\s+(?<body>\S.*?)\s*$")]
    private static partial Regex FrameLine { get; }

    [GeneratedRegex(@"\s+in\s+(?<file>.+):line\s+(?<line>\d+)$")]
    private static partial Regex SourceInfo { get; }

    [GeneratedRegex(@"^(?<original>.+)_Patch\d+$")]
    private static partial Regex PatchSuffix { get; }

    private static readonly HashSet<string> ReflectionInvokeTypes = new(StringComparer.Ordinal)
    {
        "System.Activator",
        "System.RuntimeType",
        "System.RuntimeMethodHandle",
        "System.Reflection.MethodBase",
        "System.Reflection.MethodInfo",
        "System.Reflection.RuntimeMethodInfo",
        "System.Reflection.ConstructorInfo",
        "System.Reflection.RuntimeConstructorInfo",
        "System.Reflection.ConstructorInvoker",
        "System.Reflection.MethodInvoker",
        "System.Reflection.MethodBaseInvoker"
    };

    public static bool IsFrameLine(string line) => FrameLine.IsMatch(line);

    public static CrashFrame? Parse(string line, int depth)
    {
        var match = FrameLine.Match(line);

        if (!match.Success)
            return null;

        var body = match.Groups["body"].Value;
        string? sourceFile = null;
        int? sourceLine = null;

        var source = SourceInfo.Match(body);

        if (source.Success)
        {
            sourceFile = source.Groups["file"].Value;
            sourceLine = int.Parse(source.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            body = body[..source.Index];
        }

        var open = body.IndexOf('(');
        var close = body.LastIndexOf(')');

        var qualifiedName = (open < 0 ? body : body[..open]).Trim();

        var parameters = open < 0
            ? string.Empty
            : (close > open ? body[(open + 1)..close] : body[(open + 1)..]).Trim();

        if (qualifiedName.Length == 0)
            return null;

        var (declaringType, methodName) = Split(qualifiedName);
        var patch = PatchSuffix.Match(methodName);

        return new CrashFrame(
            line.Trim(),
            qualifiedName,
            declaringType,
            methodName,
            patch.Success ? patch.Groups["original"].Value : methodName,
            parameters,
            depth,
            patch.Success,
            IsReflectionBoundary(declaringType, methodName),
            sourceFile,
            sourceLine);
    }

    // An explicit interface implementation renders as Class.Namespace.IInterface.Method, so the last
    // dot does not always separate the declaring type from the method and this can name the interface
    // instead. QualifiedName is kept whole so a caller can retry shorter prefixes against the index.
    private static (string DeclaringType, string Method) Split(string qualifiedName)
    {
        var lastDot = qualifiedName.LastIndexOf('.');

        if (lastDot <= 0)
            return (string.Empty, qualifiedName);

        // Constructors render as Type..ctor, so the separator is the dot before the leading dot.
        return qualifiedName[lastDot - 1] == '.'
            ? (qualifiedName[..(lastDot - 1)], qualifiedName[lastDot..])
            : (qualifiedName[..lastDot], qualifiedName[(lastDot + 1)..]);
    }

    private static bool IsReflectionBoundary(string declaringType, string methodName) =>
        ReflectionInvokeTypes.Contains(declaringType)
        && (methodName.StartsWith("Invoke", StringComparison.Ordinal)
            || methodName.StartsWith("UnsafeInvoke", StringComparison.Ordinal)
            || methodName.StartsWith("CreateInstance", StringComparison.Ordinal));
}
