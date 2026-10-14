// Which of FrameReader's source files the given assemblies (ShotDetector.dll, FastYuv) can run: their references to
// FrameReader's types, then every FrameReader type those types' code, fields and signatures name, read from the IL.
// Usage: reach <FrameReader.dll> <FrameReader's source folder> <assembly>...; prints the files, one per line.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

using var frameReader = new PEReader(File.OpenRead(args[0]));
var md = frameReader.GetMetadataReader();
var byName = md.TypeDefinitions.ToDictionary(h => Name(md, h));
var collector = new Collector();

foreach (var root in args[2..])
{
    using var pe = new PEReader(File.OpenRead(root));
    var rootMd = pe.GetMetadataReader();
    foreach (var h in rootMd.TypeReferences)
    {
        if (Assembly(rootMd, h) == "FrameReader" && byName.TryGetValue(Name(rootMd, h), out var def))
        {
            collector.Found.Add(def);
        }
    }
}

var reached = new HashSet<TypeDefinitionHandle>();
while (collector.Found.Count > 0)
{
    var h = collector.Found.First();
    collector.Found.Remove(h);
    if (reached.Add(h))
    {
        Walk(h);
    }

    collector.Found.ExceptWith(reached);
}

// Source files: where each reached type (or the type it is nested in) is declared; all parts of a partial type.
var sources = Directory.GetFiles(args[1], "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
    .ToDictionary(f => f, File.ReadAllText);
var files = new SortedSet<string>(StringComparer.Ordinal);
foreach (var h in reached)
{
    var outer = h;
    while (!md.GetTypeDefinition(outer).GetDeclaringType().IsNil)
    {
        outer = md.GetTypeDefinition(outer).GetDeclaringType();
    }

    var type = md.GetTypeDefinition(outer);
    if (CompilerGenerated(type) || md.GetString(type.Name).StartsWith('<'))
    {
        continue;
    }

    var name = Regex.Escape(md.GetString(type.Name).Split('`')[0]);
    var declaration = new Regex($@"\b(class|struct|record|interface|enum)\s+{name}\b|\bdelegate\b[^;]*?\b{name}\s*[<(]");
    var found = sources.Where(s => declaration.IsMatch(s.Value)).Select(s => s.Key).ToList();
    if (found.Count == 0)
    {
        Console.Error.WriteLine($"reach: no source file declares {Name(md, outer)}");
        return 1;
    }

    files.UnionWith(found.Select(f => Path.GetRelativePath(".", f).Replace('\\', '/')));
}

foreach (var f in files)
{
    Console.WriteLine(f);
}

return 0;

void Walk(TypeDefinitionHandle h)
{
    var type = md.GetTypeDefinition(h);
    Add(type.BaseType);
    foreach (var i in type.GetInterfaceImplementations())
    {
        Add(md.GetInterfaceImplementation(i).Interface);
    }

    foreach (var f in type.GetFields())
    {
        md.GetFieldDefinition(f).DecodeSignature(collector, md);
    }

    foreach (var m in type.GetMethods())
    {
        var method = md.GetMethodDefinition(m);
        method.DecodeSignature(collector, md);
        if (method.RelativeVirtualAddress == 0)
        {
            continue;
        }

        var body = frameReader.GetMethodBody(method.RelativeVirtualAddress);
        if (!body.LocalSignature.IsNil)
        {
            md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(collector, md);
        }

        foreach (var token in Tokens(body.GetILBytes()!))
        {
            Add(MetadataTokens.EntityHandle(token));
        }
    }
}

void Add(EntityHandle h)
{
    if (h.IsNil)
    {
        return;
    }

    switch (h.Kind)
    {
        case HandleKind.TypeDefinition:
            collector.Found.Add((TypeDefinitionHandle)h);
            break;
        case HandleKind.TypeSpecification:
            md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(collector, md);
            break;
        case HandleKind.MethodDefinition:
            collector.Found.Add(md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType());
            break;
        case HandleKind.FieldDefinition:
            collector.Found.Add(md.GetFieldDefinition((FieldDefinitionHandle)h).GetDeclaringType());
            break;
        case HandleKind.MemberReference:
            var member = md.GetMemberReference((MemberReferenceHandle)h);
            Add(member.Parent);
            if (member.GetKind() == MemberReferenceKind.Method)
            {
                member.DecodeMethodSignature(collector, md);
            }
            else
            {
                member.DecodeFieldSignature(collector, md);
            }

            break;
        case HandleKind.MethodSpecification:
            var spec = md.GetMethodSpecification((MethodSpecificationHandle)h);
            Add(spec.Method);
            spec.DecodeSignature(collector, md);
            break;
    }
}

bool CompilerGenerated(TypeDefinition type) => type.GetCustomAttributes().Any(a =>
{
    var ctor = md.GetCustomAttribute(a).Constructor;
    var parent = ctor.Kind == HandleKind.MemberReference ? md.GetMemberReference((MemberReferenceHandle)ctor).Parent : default;
    return parent.Kind == HandleKind.TypeReference && md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) == "CompilerGeneratedAttribute";
});

static string Name(MetadataReader md, EntityHandle h) => h.Kind switch
{
    HandleKind.TypeDefinition => DefName(md, (TypeDefinitionHandle)h),
    _ => RefName(md, (TypeReferenceHandle)h),
};

static string DefName(MetadataReader md, TypeDefinitionHandle h)
{
    var t = md.GetTypeDefinition(h);
    var outer = t.GetDeclaringType();
    return outer.IsNil ? $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}" : $"{DefName(md, outer)}/{md.GetString(t.Name)}";
}

static string RefName(MetadataReader md, TypeReferenceHandle h)
{
    var t = md.GetTypeReference(h);
    return t.ResolutionScope.Kind == HandleKind.TypeReference
        ? $"{RefName(md, (TypeReferenceHandle)t.ResolutionScope)}/{md.GetString(t.Name)}"
        : $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}";
}

static string? Assembly(MetadataReader md, TypeReferenceHandle h)
{
    var scope = md.GetTypeReference(h).ResolutionScope;
    while (scope.Kind == HandleKind.TypeReference)
    {
        scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
    }

    return scope.Kind == HandleKind.AssemblyReference ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : null;
}

static IEnumerable<int> Tokens(byte[] il)
{
    for (var i = 0; i < il.Length;)
    {
        var op = il[i] == 0xFE ? OpCodeTable.Two[il[i + 1]] : OpCodeTable.One[il[i]];
        i += op.Size;
        switch (op.OperandType)
        {
            case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType:
                yield return BitConverter.ToInt32(il, i);
                i += 4;
                break;
            case OperandType.InlineSwitch:
                i += 4 + (4 * BitConverter.ToInt32(il, i));
                break;
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                i += 1;
                break;
            case OperandType.InlineVar:
                i += 2;
                break;
            case OperandType.InlineI8 or OperandType.InlineR:
                i += 8;
                break;
            default:
                i += 4;
                break;
        }
    }
}

/// <summary>Every TypeDefinition a signature names.</summary>
internal sealed class Collector : ISignatureTypeProvider<int, MetadataReader>
{
    public HashSet<TypeDefinitionHandle> Found { get; } = [];

    public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        Found.Add(handle);
        return 0;
    }

    public int GetTypeFromSpecification(MetadataReader reader, MetadataReader genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => 0;

    public int GetArrayType(int elementType, ArrayShape shape) => 0;

    public int GetByReferenceType(int elementType) => 0;

    public int GetFunctionPointerType(MethodSignature<int> signature) => 0;

    public int GetGenericInstantiation(int genericType, System.Collections.Immutable.ImmutableArray<int> typeArguments) => 0;

    public int GetGenericMethodParameter(MetadataReader genericContext, int index) => 0;

    public int GetGenericTypeParameter(MetadataReader genericContext, int index) => 0;

    public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => 0;

    public int GetPinnedType(int elementType) => 0;

    public int GetPointerType(int elementType) => 0;

    public int GetPrimitiveType(PrimitiveTypeCode typeCode) => 0;

    public int GetSZArrayType(int elementType) => 0;
}

/// <summary>IL opcodes by their first (or, after 0xFE, second) byte.</summary>
internal static class OpCodeTable
{
    public static readonly OpCode[] One = new OpCode[256];
    public static readonly OpCode[] Two = new OpCode[256];

    static OpCodeTable()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)f.GetValue(null)!;
            var value = (ushort)op.Value;
            if (op.Size == 1)
            {
                One[value] = op;
            }
            else
            {
                Two[value & 0xFF] = op;
            }
        }
    }
}
