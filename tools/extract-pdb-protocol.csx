// extract-pdb-protocol.csx - Extract all protocol enums and structs from a PDB type dump.
//
// Usage:
//   dotnet script tools/extract-pdb-protocol.csx -- <pdb-dump-file> <output-dir>
//
// Single-pass parser: reads the entire dump once, builds hash-indexed lookup
// tables, then resolves struct fields recursively.
//
// The input file should be created with:
//   llvm-pdbutil dump -types <file.pdb> > dump.txt

using System.Text.Json;
using System.Text.RegularExpressions;

// --- Built-in PDB type index -> C type name ---
var BuiltinTypes = new Dictionary<int, string>
{
    [0x0003] = "void",
    [0x0010] = "char",
    [0x0011] = "short",
    [0x0012] = "long",
    [0x0013] = "long long",
    [0x0020] = "unsigned char",
    [0x0021] = "unsigned short",
    [0x0022] = "unsigned long",
    [0x0023] = "unsigned __int64",
    [0x0030] = "bool",
    [0x0040] = "float",
    [0x0041] = "double",
    [0x0070] = "char",
    [0x0071] = "wchar_t",
    [0x0074] = "int",
    [0x0075] = "unsigned int",
    [0x0403] = "void*",
    [0x0410] = "char*",
    [0x0474] = "int*",
};

var BuiltinSizes = new Dictionary<int, int>
{
    [0x0010] = 1, [0x0020] = 1, [0x0030] = 1, [0x0070] = 1,
    [0x0011] = 2, [0x0021] = 2, [0x0071] = 2,
    [0x0012] = 4, [0x0022] = 4, [0x0074] = 4, [0x0075] = 4, [0x0040] = 4,
    [0x0013] = 8, [0x0023] = 8, [0x0041] = 8,
    [0x0403] = 4, [0x0410] = 4, [0x0474] = 4,
};

// --- Data structures ---
record EnumEntry(string Name, int Value);

record FieldMember(string Name, string TypeIdx, string TypeName, int Offset, bool IsBase = false);

record FieldList(List<FieldMember> Members, List<EnumEntry> Enumerates);

record StructDef(string Name, int SizeOf, string FieldListIdx, string ForwardRef);

record ArrayDef(int Size, string ElementType);

record ModifierDef(string Referent, string Modifier);

record PointerDef(string Referent);

// --- Global indexes (populated in single pass) ---
var fieldlists = new Dictionary<string, FieldList>();
var structs = new Dictionary<string, StructDef>();
var enums = new Dictionary<string, (string Name, string FieldListIdx)>();
var arrays = new Dictionary<string, ArrayDef>();
var modifiers = new Dictionary<string, ModifierDef>();
var pointers = new Dictionary<string, PointerDef>();

// --- Parse args ---
if (Args.Count < 2)
{
    Console.WriteLine("Usage: dotnet script tools/extract-pdb-protocol.csx -- <pdb-dump-file> <output-dir>");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet script tools/extract-pdb-protocol.csx -- ~/test.txt docs/extracted/worldmanager");
    return;
}

var inputFile = Args[0];
var outputDir = Args[1];

if (!File.Exists(inputFile))
{
    Console.Error.WriteLine($"Error: Input file not found: {inputFile}");
    return;
}

Directory.CreateDirectory(Path.Combine(outputDir, "enums"));
Directory.CreateDirectory(Path.Combine(outputDir, "structs"));

Console.WriteLine("=== PDB Protocol Extractor ===");
Console.WriteLine($"Input:  {inputFile}");
Console.WriteLine($"Output: {outputDir}");
Console.WriteLine();

// --- Step 0: Read and clean input ---
Console.Write("Reading input... ");
var rawBytes = File.ReadAllBytes(inputFile);

// Strip null bytes for UTF-16 compatibility
string text;
if (rawBytes.Take(100).Any(b => b == 0))
{
    // UTF-16: strip null bytes
    var cleaned = rawBytes.Where(b => b != 0 && b != '\r').ToArray();
    text = System.Text.Encoding.UTF8.GetString(cleaned);
}
else
{
    text = System.Text.Encoding.UTF8.GetString(rawBytes).Replace("\r", "");
}

var lines = text.Split('\n');
Console.WriteLine($"{lines.Length} lines");

// --- Step 1: Single-pass parse ---
Console.WriteLine();
Console.Write("Parsing... ");

var reTypeHeader = new Regex(@"^\s*(0x[0-9A-Fa-f]+)\s*\|\s*(\w+)\s*\[size\s*=\s*(\d+)\]\s*(?:`([^`]*)`)?");
var reFieldlistHeader = new Regex(@"^\s*(0x[0-9A-Fa-f]+)\s*\|\s*LF_FIELDLIST\s*\[size\s*=\s*(\d+)\]");
var reEnumerate = new Regex(@"LF_ENUMERATE\s*\[(\w+)\s*=\s*(\d+)\]");
var reMember = new Regex(@"LF_MEMBER\s*\[name\s*=\s*`([^`]*)`.*?Type\s*=\s*(0x[0-9A-Fa-f]+)(?:\s*\(([^)]*)\))?.*?offset\s*=\s*(\d+)");
var reBclass = new Regex(@"LF_BCLASS.*?type\s*=\s*(0x[0-9A-Fa-f]+).*?offset\s*=\s*(\d+)");
var reFieldListRef = new Regex(@"field list:\s*(0x[0-9A-Fa-f]+)");
var reSizeof = new Regex(@"sizeof\s+(\d+)");
var reForwardRef = new Regex(@"forward ref\s*\((?:->|<-)\s*(0x[0-9A-Fa-f]+)\)");
var reArray = new Regex(@"size:\s*(\d+).*?element type:\s*(0x[0-9A-Fa-f]+)");
var reModifier = new Regex(@"referent\s*=\s*(0x[0-9A-Fa-f]+).*?modifiers\s*=\s*(\w+)");
var rePointer = new Regex(@"referent\s*=\s*(0x[0-9A-Fa-f]+)");

string currentFlIdx = null;
var currentFlMembers = new List<FieldMember>();
var currentFlEnumerates = new List<EnumEntry>();

void SaveCurrentFieldList()
{
    if (currentFlIdx != null)
    {
        fieldlists[currentFlIdx] = new FieldList(
            new List<FieldMember>(currentFlMembers),
            new List<EnumEntry>(currentFlEnumerates)
        );
        currentFlIdx = null;
        currentFlMembers.Clear();
        currentFlEnumerates.Clear();
    }
}

int i = 0;
while (i < lines.Length)
{
    var line = lines[i];
    i++;

    // Check for fieldlist header FIRST (reTypeHeader also matches LF_FIELDLIST lines)
    var flm = reFieldlistHeader.Match(line);
    if (flm.Success)
    {
        SaveCurrentFieldList();
        currentFlIdx = flm.Groups[1].Value.ToLower();
        continue;
    }

    // Check for type record header
    var m = reTypeHeader.Match(line);
    if (m.Success)
    {
        SaveCurrentFieldList();

        var idx = m.Groups[1].Value.ToLower();
        var kind = m.Groups[2].Value;
        var name = m.Groups[4].Success ? m.Groups[4].Value : "";

        if (kind == "LF_STRUCTURE" || kind == "LF_CLASS" || kind == "LF_UNION")
        {
            string flRef = null, fwdRef = null;
            int sz = 0;

            while (i < lines.Length)
            {
                var subline = lines[i];
                if (reTypeHeader.IsMatch(subline) || reFieldlistHeader.IsMatch(subline)) break;

                var fm = reForwardRef.Match(subline);
                if (fm.Success) fwdRef = fm.Groups[1].Value.ToLower();

                var fl = reFieldListRef.Match(subline);
                if (fl.Success && fl.Groups[1].Value.ToLower() != "0x0000")
                    flRef = fl.Groups[1].Value.ToLower();

                var sm = reSizeof.Match(subline);
                if (sm.Success) sz = int.Parse(sm.Groups[1].Value);

                i++;
            }

            // Store the definition (non-forward-ref with field list)
            if (fwdRef == null && flRef != null)
                structs[idx] = new StructDef(name, sz, flRef, null);
            // Store forward refs for later resolution
            else if (fwdRef != null)
                structs[idx] = new StructDef(name, 0, null, fwdRef);

            continue;
        }
        else if (kind == "LF_ENUM")
        {
            string flRef = null;
            while (i < lines.Length)
            {
                var subline = lines[i];
                if (reTypeHeader.IsMatch(subline) || reFieldlistHeader.IsMatch(subline)) break;

                var fl = reFieldListRef.Match(subline);
                if (fl.Success && fl.Groups[1].Value.ToLower() != "0x0000")
                    flRef = fl.Groups[1].Value.ToLower();
                i++;
            }
            enums[idx] = (name, flRef);
            continue;
        }
        else if (kind == "LF_ARRAY")
        {
            int arrSize = 0;
            string elemType = null;

            var am = reArray.Match(line);
            if (am.Success)
            {
                arrSize = int.Parse(am.Groups[1].Value);
                elemType = am.Groups[2].Value.ToLower();
            }
            else
            {
                while (i < lines.Length)
                {
                    var subline = lines[i];
                    if (reTypeHeader.IsMatch(subline) || reFieldlistHeader.IsMatch(subline)) break;
                    am = reArray.Match(subline);
                    if (am.Success)
                    {
                        arrSize = int.Parse(am.Groups[1].Value);
                        elemType = am.Groups[2].Value.ToLower();
                    }
                    i++;
                }
            }
            if (elemType != null)
                arrays[idx] = new ArrayDef(arrSize, elemType);
            continue;
        }
        else if (kind == "LF_MODIFIER")
        {
            var mm = reModifier.Match(line);
            if (!mm.Success && i < lines.Length)
            {
                mm = reModifier.Match(lines[i]);
                i++;
            }
            if (mm.Success)
                modifiers[idx] = new ModifierDef(mm.Groups[1].Value.ToLower(), mm.Groups[2].Value);
            continue;
        }
        else if (kind == "LF_POINTER")
        {
            var pm = rePointer.Match(line);
            if (!pm.Success && i < lines.Length)
            {
                pm = rePointer.Match(lines[i]);
                i++;
            }
            if (pm.Success)
                pointers[idx] = new PointerDef(pm.Groups[1].Value.ToLower());
            continue;
        }
        else
        {
            // Skip unknown record types
            while (i < lines.Length && !reTypeHeader.IsMatch(lines[i]) && !reFieldlistHeader.IsMatch(lines[i]))
                i++;
            continue;
        }
    }

    // Inside a fieldlist
    if (currentFlIdx != null)
    {
        var em = reEnumerate.Match(line);
        if (em.Success)
        {
            currentFlEnumerates.Add(new EnumEntry(em.Groups[1].Value, int.Parse(em.Groups[2].Value)));
            continue;
        }

        var mm = reMember.Match(line);
        if (mm.Success)
        {
            currentFlMembers.Add(new FieldMember(
                mm.Groups[1].Value,
                mm.Groups[2].Value.ToLower(),
                mm.Groups[3].Success ? mm.Groups[3].Value : null,
                int.Parse(mm.Groups[4].Value)
            ));
            continue;
        }

        var bm = reBclass.Match(line);
        if (bm.Success)
        {
            currentFlMembers.Add(new FieldMember(
                "__base",
                bm.Groups[1].Value.ToLower(),
                null,
                int.Parse(bm.Groups[2].Value),
                IsBase: true
            ));
            continue;
        }
    }
}

SaveCurrentFieldList();

Console.WriteLine("done.");
Console.WriteLine($"  {fieldlists.Count} fieldlists, {structs.Count} structs, {enums.Count} enums, {arrays.Count} arrays");

// --- Resolve forward refs: if a struct is a forward ref, point to the actual definition ---
Console.Write("Resolving forward refs... ");
int resolvedCount = 0;
foreach (var (idx, s) in structs.ToList())
{
    if (s.ForwardRef != null && structs.TryGetValue(s.ForwardRef, out var target) && target.ForwardRef == null)
    {
        // Replace forward ref with resolved entry that keeps the original name but gets field list + sizeof
        structs[idx] = new StructDef(s.Name, target.SizeOf, target.FieldListIdx, null);
        resolvedCount++;
    }
}
Console.WriteLine($"{resolvedCount} resolved");

// --- Type resolution helpers ---
string ResolveType(string typeIdx, int depth = 0)
{
    if (depth > 10) return $"[{typeIdx}]";

    var idxInt = Convert.ToInt32(typeIdx, 16);
    if (BuiltinTypes.TryGetValue(idxInt, out var builtinName))
        return builtinName;
    if (structs.TryGetValue(typeIdx, out var s))
    {
        // Follow forward ref if needed
        if (s.ForwardRef != null && structs.TryGetValue(s.ForwardRef, out var target))
            return target.Name;
        return s.Name;
    }
    if (arrays.TryGetValue(typeIdx, out var arr))
    {
        var elemType = ResolveType(arr.ElementType, depth + 1);
        var elemSize = GetTypeSize(arr.ElementType);
        if (elemSize > 0)
            return $"{elemType}[{arr.Size / elemSize}]";
        return $"{elemType}[?{arr.Size}B]";
    }
    if (modifiers.TryGetValue(typeIdx, out var mod))
        return ResolveType(mod.Referent, depth + 1);
    if (pointers.TryGetValue(typeIdx, out var ptr))
        return ResolveType(ptr.Referent, depth + 1) + "*";
    return $"[{typeIdx}]";
}

int GetTypeSize(string typeIdx)
{
    var idxInt = Convert.ToInt32(typeIdx, 16);
    if (BuiltinSizes.TryGetValue(idxInt, out var sz)) return sz;
    if (structs.TryGetValue(typeIdx, out var s))
    {
        if (s.SizeOf > 0) return s.SizeOf;
        // Follow forward ref
        if (s.ForwardRef != null && structs.TryGetValue(s.ForwardRef, out var target))
            return target.SizeOf;
        return 0;
    }
    if (arrays.TryGetValue(typeIdx, out var arr)) return arr.Size;
    if (modifiers.TryGetValue(typeIdx, out var mod)) return GetTypeSize(mod.Referent);
    if (pointers.ContainsKey(typeIdx)) return 4; // 32-bit
    return 0;
}

// --- Find PROTOCOL_COMMAND department table ---
Console.WriteLine();
Console.WriteLine("--- Finding PROTOCOL_COMMAND department table ---");

FieldList protocolCommandFl = null;
string protocolCommandFlIdx = null;

foreach (var (flIdx, fl) in fieldlists)
{
    if (fl.Enumerates.Count < 10) continue;
    var nameMap = fl.Enumerates.ToDictionary(e => e.Name, e => e.Value);
    if (nameMap.GetValueOrDefault("NC_NULL") == 0 &&
        nameMap.GetValueOrDefault("NC_LOG") == 1 &&
        nameMap.GetValueOrDefault("NC_MISC") == 2)
    {
        protocolCommandFl = fl;
        protocolCommandFlIdx = flIdx;
        break;
    }
}

if (protocolCommandFl == null)
{
    Console.Error.WriteLine("ERROR: Could not find PROTOCOL_COMMAND enum!");
    return;
}

Console.WriteLine($"  Found at fieldlist {protocolCommandFlIdx}: {protocolCommandFl.Enumerates.Count} departments");

var departments = new Dictionary<string, int>();
foreach (var e in protocolCommandFl.Enumerates)
    departments[e.Name.Replace("NC_", "")] = e.Value;

var deptNames = departments.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();

// Write department IDs
using (var f = File.CreateText(Path.Combine(outputDir, "department-ids.txt")))
{
    foreach (var dept in deptNames)
        f.WriteLine($"NC_{dept} = {departments[dept]}");
}

foreach (var dept in deptNames)
    Console.WriteLine($"  0x{departments[dept]:X2} {dept}");

// --- Collect all per-department enum values ---
Console.WriteLine();
Console.WriteLine("--- Collecting per-department enum values ---");

var allEnums = new Dictionary<string, int>();
foreach (var fl in fieldlists.Values)
{
    foreach (var e in fl.Enumerates)
    {
        if (e.Name.StartsWith("NC_") && e.Name.IndexOf('_', 3) > 0)
            allEnums[e.Name] = e.Value;
    }
}

Console.WriteLine($"  {allEnums.Count} total opcode enum values");

// Group by department (longest match first)
var deptNamesSorted = deptNames.OrderByDescending(d => d.Length).ToList();
var deptEnums = new Dictionary<string, List<(string Name, int Value)>>();

foreach (var (enumName, enumVal) in allEnums)
{
    var stripped = enumName[3..]; // remove "NC_"
    string bestDept = null;
    foreach (var dept in deptNamesSorted)
    {
        if (stripped.StartsWith(dept + "_"))
        {
            bestDept = dept;
            break; // Already sorted by length desc, first match is longest
        }
    }
    bestDept ??= "_UNCATEGORIZED";
    if (!deptEnums.ContainsKey(bestDept))
        deptEnums[bestDept] = new List<(string, int)>();
    deptEnums[bestDept].Add((enumName, enumVal));
}

// Write per-department enum files
foreach (var dept in deptNames)
{
    if (!deptEnums.ContainsKey(dept)) continue;
    var entries = deptEnums[dept].OrderBy(e => e.Value).ToList();
    var hex = $"0x{departments[dept]:X2}";
    using var f = File.CreateText(Path.Combine(outputDir, "enums", $"{dept}.txt"));
    f.WriteLine($"# {dept} ({hex}) - {entries.Count} opcodes");
    f.WriteLine($"# Full opcode = {hex} << 8 | cmd_value");
    f.WriteLine("#");
    foreach (var (name, val) in entries)
        f.WriteLine($"{name} = {val}");
    Console.WriteLine($"  {dept} ({hex}): {entries.Count} opcodes");
}

// Write all-enums.json
var enumsJson = new Dictionary<string, object>();
foreach (var dept in deptNames)
{
    if (!deptEnums.ContainsKey(dept)) continue;
    enumsJson[dept] = new
    {
        id = departments[dept],
        hex = $"0x{departments[dept]:X2}",
        opcodes = deptEnums[dept].OrderBy(e => e.Value)
            .ToDictionary(e => e.Name, e => e.Value)
    };
}
File.WriteAllText(
    Path.Combine(outputDir, "all-enums.json"),
    JsonSerializer.Serialize(enumsJson, new JsonSerializerOptions { WriteIndented = true })
);

// --- Collect all PROTO_NC_* structs ---
Console.WriteLine();
Console.WriteLine("--- Collecting protocol structs ---");

string ClassifyDept(string structName)
{
    var stripped = structName.Replace("PROTO_NC_", "");
    string best = null;
    foreach (var dept in deptNamesSorted)
    {
        if (stripped.StartsWith(dept + "_"))
        {
            best = dept;
            break;
        }
    }
    return best ?? stripped.Split('_')[0];
}

List<object> ResolveFields(string fieldListIdx)
{
    if (fieldListIdx == null || !fieldlists.TryGetValue(fieldListIdx, out var fl))
        return new List<object>();

    var result = new List<object>();
    foreach (var member in fl.Members)
    {
        var typeStr = ResolveType(member.TypeIdx);
        var size = GetTypeSize(member.TypeIdx);
        var field = new Dictionary<string, object>
        {
            ["name"] = member.Name,
            ["offset"] = member.Offset,
            ["type"] = typeStr,
            ["type_idx"] = member.TypeIdx,
            ["size"] = size,
        };
        if (member.IsBase) field["is_base"] = true;
        result.Add(field);
    }
    return result;
}

var protoStructs = new Dictionary<string, object>();
var deptStructs = new Dictionary<string, List<(string Name, int SizeOf, List<object> Fields)>>();

foreach (var (idx, s) in structs)
{
    if (!s.Name.StartsWith("PROTO_NC_")) continue;
    if (s.Name.Contains("::")) continue; // Skip scoped/nested types
    if (s.FieldListIdx == null) continue; // Skip unresolved forward refs

    var dept = ClassifyDept(s.Name);
    var fields = ResolveFields(s.FieldListIdx);

    if (!protoStructs.ContainsKey(s.Name) || s.SizeOf > 0)
    {
        protoStructs[s.Name] = new { s.Name, s.SizeOf, department = dept, fields };

        if (!deptStructs.ContainsKey(dept))
            deptStructs[dept] = new List<(string, int, List<object>)>();
        deptStructs[dept].Add((s.Name, s.SizeOf, fields));
    }
}

// Deduplicate
foreach (var dept in deptStructs.Keys.ToList())
{
    var seen = new HashSet<string>();
    deptStructs[dept] = deptStructs[dept]
        .Where(s => seen.Add(s.Name))
        .OrderBy(s => s.Name)
        .ToList();
}

Console.WriteLine($"  {protoStructs.Count} unique PROTO_NC_* structs");

// Write per-department struct files
foreach (var dept in deptNames)
{
    if (!deptStructs.ContainsKey(dept)) continue;
    using var f = File.CreateText(Path.Combine(outputDir, "structs", $"{dept}.txt"));
    foreach (var (name, sizeOf, fields) in deptStructs[dept])
    {
        f.WriteLine($"## {name} (sizeof {sizeOf})");
        if (fields.Count > 0)
        {
            foreach (var field in fields)
            {
                var fd = (Dictionary<string, object>)field;
                var prefix = fd.ContainsKey("is_base") ? "  base " : "  ";
                f.WriteLine($"{prefix}+{fd["offset"]}: {fd["type"]} {fd["name"]}");
            }
        }
        else
        {
            f.WriteLine("  (no fields)");
        }
        f.WriteLine();
    }
    Console.WriteLine($"  {dept}: {deptStructs[dept].Count} structs");
}

// --- Collect all referenced types recursively ---
Console.Write("Collecting referenced types... ");

var referencedTypes = new Dictionary<string, object>(); // name -> type info

void CollectReferencedType(string typeIdx, HashSet<string> visited)
{
    if (visited.Contains(typeIdx)) return;
    visited.Add(typeIdx);

    var idxInt = Convert.ToInt32(typeIdx, 16);
    if (BuiltinTypes.ContainsKey(idxInt)) return;

    if (structs.TryGetValue(typeIdx, out var s))
    {
        if (s.Name.Contains("::")) return; // Skip scoped types

        var fields = ResolveFields(s.FieldListIdx);
        if (!referencedTypes.ContainsKey(s.Name))
        {
            referencedTypes[s.Name] = new
            {
                s.Name,
                s.SizeOf,
                kind = "struct",
                fields
            };
        }
        // Recurse into field types
        if (s.FieldListIdx != null && fieldlists.TryGetValue(s.FieldListIdx, out var fl))
        {
            foreach (var member in fl.Members)
                CollectReferencedType(member.TypeIdx, visited);
        }
        return;
    }

    if (arrays.TryGetValue(typeIdx, out var arr))
    {
        CollectReferencedType(arr.ElementType, visited);
        return;
    }

    if (modifiers.TryGetValue(typeIdx, out var mod))
    {
        CollectReferencedType(mod.Referent, visited);
        return;
    }

    if (pointers.TryGetValue(typeIdx, out var ptr))
    {
        CollectReferencedType(ptr.Referent, visited);
        return;
    }
}

// Walk all PROTO_NC_* struct fields and collect referenced types
var visitedTypes = new HashSet<string>();
foreach (var (idx, s) in structs)
{
    if (!s.Name.StartsWith("PROTO_NC_")) continue;
    if (s.Name.Contains("::")) continue;
    if (s.FieldListIdx == null) continue;

    if (fieldlists.TryGetValue(s.FieldListIdx, out var fl))
    {
        foreach (var member in fl.Members)
            CollectReferencedType(member.TypeIdx, visitedTypes);
    }
}

// Filter out PROTO_NC_* (already in protoStructs) to get only helper/shared types
var helperTypes = referencedTypes
    .Where(kv => !((string)((dynamic)kv.Value).Name).StartsWith("PROTO_NC_"))
    .ToDictionary(kv => kv.Key, kv => kv.Value);

Console.WriteLine($"{helperTypes.Count} helper types (Name4, PROTO_KQ_INFO, etc.)");

// Write all-structs.json with referenced types included
var fullOutput = new Dictionary<string, object>
{
    ["protocol_structs"] = protoStructs,
    ["referenced_types"] = helperTypes,
};

File.WriteAllText(
    Path.Combine(outputDir, "all-structs.json"),
    JsonSerializer.Serialize(fullOutput, new JsonSerializerOptions { WriteIndented = true })
);

// --- Generate department index ---
Console.WriteLine();
Console.WriteLine("--- Generating department index ---");

using (var f = File.CreateText(Path.Combine(outputDir, "department-index.md")))
{
    f.WriteLine("# Protocol Department Index");
    f.WriteLine();
    f.WriteLine($"Generated from: {Path.GetFileName(inputFile)}");
    f.WriteLine();
    f.WriteLine("| ID | Hex | Department | Enums | Structs |");
    f.WriteLine("|----|-----|-----------|-------|--------|");

    int totalEnums = 0, totalStructs = 0;
    foreach (var dept in deptNames)
    {
        var hex = $"0x{departments[dept]:X2}";
        var enumCount = deptEnums.GetValueOrDefault(dept)?.Count ?? 0;
        var structCount = deptStructs.GetValueOrDefault(dept)?.Count ?? 0;
        totalEnums += enumCount;
        totalStructs += structCount;
        f.WriteLine($"| {departments[dept]} | {hex} | {dept} | {enumCount} | {structCount} |");
    }
    f.WriteLine();
    f.WriteLine($"**Totals:** {totalEnums} enum values, {totalStructs} struct definitions");
}

Console.WriteLine();
Console.WriteLine("=== Done ===");
Console.WriteLine($"Output: {outputDir}");
Console.WriteLine($"  department-index.md  - {deptNames.Count} departments");
Console.WriteLine($"  enums/               - {deptNames.Count(d => deptEnums.ContainsKey(d))} department files");
Console.WriteLine($"  structs/             - {deptNames.Count(d => deptStructs.ContainsKey(d))} department files");
Console.WriteLine($"  all-enums.json       - {allEnums.Count} enum values (JSON)");
Console.WriteLine($"  all-structs.json     - {protoStructs.Count} struct definitions (JSON)");
