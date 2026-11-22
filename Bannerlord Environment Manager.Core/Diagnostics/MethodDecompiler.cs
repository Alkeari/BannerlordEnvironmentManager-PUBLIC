using BannerlordEnvironmentManager.Core.Localization;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace BannerlordEnvironmentManager.Core.Diagnostics;

public sealed record DecompiledMethod(
    bool Succeeded,
    string Message,
    string Source = "",
    string AssemblyPath = "",
    string TypeFullName = "",
    string MethodName = "",
    int OverloadCount = 0,
    IReadOnlyList<string> OtherAssemblies = null!)
{
    public IReadOnlyList<string> OtherAssemblies { get; init; } = OtherAssemblies ?? [];
}

// Reads a method's IL back as C# so a person can see what the failing code does. It says nothing about
// which mod is at fault: attribution is decided by the crash report's own evidence, and reading a body
// neither adds evidence nor takes any away.
//
// Metadata only, through ICSharpCode.Decompiler's own PEFile reader. Nothing here loads an assembly into
// this process, and a mod assembly must never be loaded to be read.
public static class MethodDecompiler
{
    private static readonly DecompilerSettings Settings = new(LanguageVersion.CSharp10_0)
    {
        ThrowOnAssemblyResolveErrors = false,
        ShowXmlDocumentation = false,
        RemoveDeadCode = false
    };

    public static DecompiledMethod Decompile(
        string assemblyPath,
        string typeFullName,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            return new DecompiledMethod(
                false, Strings.Current.Format("Core.Diagnostics.MethodDecompiler.NoAssembly", assemblyPath));
        }

        if (string.IsNullOrWhiteSpace(typeFullName) || string.IsNullOrWhiteSpace(methodName))
            return new DecompiledMethod(false, Strings.Current["Core.Diagnostics.MethodDecompiler.NoLookup"]);

        try
        {
            var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, targetFramework: null);
            var decompiler = new CSharpDecompiler(assemblyPath, resolver, Settings)
            {
                CancellationToken = cancellationToken
            };

            var type = decompiler.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(typeFullName));

            if (type is null)
            {
                return new DecompiledMethod(
                    false,
                    Strings.Current.Format(
                        "Core.Diagnostics.MethodDecompiler.NoType", Path.GetFileName(assemblyPath), typeFullName),
                    AssemblyPath: assemblyPath,
                    TypeFullName: typeFullName,
                    MethodName: methodName);
            }

            var methods = type.Methods
                .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                .Where(m => !m.MetadataToken.IsNil)
                .ToList();

            if (methods.Count == 0)
            {
                var known = type.Methods.Select(m => m.Name).Distinct(StringComparer.Ordinal).Take(12).ToList();

                return new DecompiledMethod(
                    false,
                    Strings.Current.Format(
                        known.Count == 12
                            ? "Core.Diagnostics.MethodDecompiler.NoMethod.AndMore"
                            : "Core.Diagnostics.MethodDecompiler.NoMethod.Listed",
                        typeFullName,
                        methodName,
                        string.Join(", ", known)),
                    AssemblyPath: assemblyPath,
                    TypeFullName: typeFullName,
                    MethodName: methodName);
            }

            var source = decompiler.DecompileAsString([.. methods.Select(m => m.MetadataToken)]);

            if (string.IsNullOrWhiteSpace(source))
            {
                return new DecompiledMethod(
                    false,
                    Strings.Current.Format("Core.Diagnostics.MethodDecompiler.EmptyBody", typeFullName, methodName),
                    AssemblyPath: assemblyPath,
                    TypeFullName: typeFullName,
                    MethodName: methodName);
            }

            var message = methods.Count == 1
                ? Strings.Current.Format("Core.Diagnostics.MethodDecompiler.Read.Single", typeFullName, methodName, assemblyPath)
                : Strings.Current.Format(
                    "Core.Diagnostics.MethodDecompiler.Read.Overloads", typeFullName, methodName, methods.Count, assemblyPath);

            return new DecompiledMethod(
                true,
                IsUnreadableBody(source)
                    ? message + Strings.Current["Core.Diagnostics.MethodDecompiler.Obfuscated"]
                    : message,
                source,
                assemblyPath,
                typeFullName,
                methodName,
                methods.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // A ConfuserEx-protected mod assembly is the normal case here, not the exceptional one: control
        // flow it rewrote does not decompile, and saying so plainly beats an empty pane.
        catch (Exception ex)
        {
            return new DecompiledMethod(
                false,
                Strings.Current.Format(
                    "Core.Diagnostics.MethodDecompiler.DecompileFailed", typeFullName, methodName, ex.Message),
                AssemblyPath: assemblyPath,
                TypeFullName: typeFullName,
                MethodName: methodName);
        }
    }

    // ConfuserEx rewrites method headers into something the reader rejects, and the decompiler emits the
    // reason as a comment in place of the body rather than throwing. Half the mods on a real install are
    // protected this way, so that outcome is reported instead of being passed off as a decompile.
    private static bool IsUnreadableBody(string source) =>
        source.Contains("Invalid MethodBodyBlock", StringComparison.Ordinal)
        || source.Contains("Could not decompile", StringComparison.Ordinal);

    public static DecompiledMethod DecompileFrame(
        AssemblyIndex index,
        CrashFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.DeclaringTypeFullName.Length == 0)
        {
            return new DecompiledMethod(
                false,
                Strings.Current.Format("Core.Diagnostics.MethodDecompiler.NoSplit", frame.QualifiedName));
        }

        var declaring = index.FindDeclaring(frame.DeclaringTypeFullName);

        if (declaring.Count == 0)
        {
            return new DecompiledMethod(
                false,
                Strings.Current.Format(
                    "Core.Diagnostics.MethodDecompiler.NotIndexed", frame.DeclaringTypeFullName),
                TypeFullName: frame.DeclaringTypeFullName,
                MethodName: frame.OriginalMethodName);
        }

        // Harmony renames the original to Method_Patch0 and puts its own wrapper under the original name.
        // The body worth reading is the one the mod's IL was woven into, which is the original name.
        var result = Decompile(declaring[0].Path, frame.DeclaringTypeFullName, frame.OriginalMethodName, cancellationToken);

        return declaring.Count == 1
            ? result
            : result with { OtherAssemblies = [.. declaring.Skip(1).Select(a => a.Path)] };
    }
}
