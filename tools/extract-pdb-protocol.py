#!/usr/bin/env python3
"""
extract-pdb-protocol.py - Extract all protocol enums and structs from a PDB type dump.

Single-pass parser that builds an in-memory index of all type records, then
resolves struct fields recursively (so referenced types like NETCOMMAND or
char arrays are included in the output).

Usage:
    python3 tools/extract-pdb-protocol.py <pdb-dump-file> <output-dir>

The input file should be created with:
    llvm-pdbutil dump -types <file.pdb> > dump.txt

Output:
    <output-dir>/department-index.md   - Department summary table
    <output-dir>/enums/<DEPT>.txt      - Per-department opcode enum values
    <output-dir>/structs/<DEPT>.txt    - Per-department struct definitions with resolved fields
    <output-dir>/all-enums.json        - All enums as JSON (for code generation)
    <output-dir>/all-structs.json      - All structs as JSON with resolved types (for code generation)
    <output-dir>/types.json            - Full type index (for debugging)
"""

import sys
import os
import re
import json
from collections import defaultdict

# --- PDB built-in type names ---
BUILTIN_TYPES = {
    0x0003: "void",
    0x0010: "char",         # signed char (int8)
    0x0011: "short",        # int16
    0x0012: "long",         # int32
    0x0013: "long long",    # int64
    0x0020: "unsigned char",      # uint8 / byte
    0x0021: "unsigned short",     # uint16
    0x0022: "unsigned long",      # uint32
    0x0023: "unsigned __int64",   # uint64
    0x0030: "bool",
    0x0040: "float",
    0x0041: "double",
    0x0070: "char",         # narrow char
    0x0071: "wchar_t",
    0x0074: "int",          # int32
    0x0075: "unsigned int", # uint32
    0x0403: "void*",
    0x0410: "char*",
    0x0474: "int*",
}

# C type -> C# type
CSHARP_TYPES = {
    "void": "void",
    "char": "sbyte",
    "short": "short",
    "long": "int",
    "long long": "long",
    "unsigned char": "byte",
    "unsigned short": "ushort",
    "unsigned long": "uint",
    "unsigned __int64": "ulong",
    "bool": "bool",
    "float": "float",
    "double": "double",
    "int": "int",
    "unsigned int": "uint",
    "void*": "nint",
    "wchar_t": "char",
}


def parse_pdb_dump(filepath):
    """Single-pass parse of llvm-pdbutil dump -types output.

    Builds indexes of:
    - type_records: {hex_idx: record_dict}
    - fieldlists: {hex_idx: [member_dicts]}
    - enums: {hex_idx: {name, underlying_type, field_list_idx, members}}
    - structs: {hex_idx: {name, sizeof, field_list_idx, forward_ref}}
    """

    print("  Parsing PDB dump...")

    # Read file, strip null bytes for UTF-16 compatibility
    with open(filepath, 'rb') as f:
        raw = f.read()

    # Strip null bytes (UTF-16 -> ASCII conversion)
    if b'\x00' in raw[:100]:
        raw = raw.replace(b'\x00', b'')

    text = raw.decode('utf-8', errors='replace')
    lines = text.splitlines()

    print(f"  {len(lines)} lines")

    # Regex patterns
    re_type_header = re.compile(r'^\s*(0x[0-9A-Fa-f]+)\s*\|\s*(\w+)\s*\[size\s*=\s*(\d+)\]\s*(?:`([^`]*)`)?')
    re_fieldlist_header = re.compile(r'^\s*(0x[0-9A-Fa-f]+)\s*\|\s*LF_FIELDLIST\s*\[size\s*=\s*(\d+)\]')
    re_enumerate = re.compile(r'LF_ENUMERATE\s*\[(\w+)\s*=\s*(\d+)\]')
    re_member = re.compile(r'LF_MEMBER\s*\[name\s*=\s*`([^`]*)`.*?Type\s*=\s*(0x[0-9A-Fa-f]+)(?:\s*\(([^)]*)\))?.*?offset\s*=\s*(\d+)')
    re_bclass = re.compile(r'LF_BCLASS.*?type\s*=\s*(0x[0-9A-Fa-f]+).*?offset\s*=\s*(\d+)')
    re_onemethod = re.compile(r'LF_ONEMETHOD\s*\[name\s*=\s*`([^`]*)`\]')
    re_field_list_ref = re.compile(r'field list:\s*(0x[0-9A-Fa-f]+)')
    re_sizeof = re.compile(r'sizeof\s+(\d+)')
    re_forward_ref = re.compile(r'forward ref\s*\((?:->|<-)\s*(0x[0-9A-Fa-f]+)\)')
    re_array = re.compile(r'size:\s*(\d+).*?element type:\s*(0x[0-9A-Fa-f]+)')
    re_modifier = re.compile(r'referent\s*=\s*(0x[0-9A-Fa-f]+).*?modifiers\s*=\s*(\w+)')
    re_pointer = re.compile(r'referent\s*=\s*(0x[0-9A-Fa-f]+)')

    # Storage
    fieldlists = {}       # hex_idx -> [members]
    structs = {}          # hex_idx -> {name, sizeof, field_list}
    enums = {}            # hex_idx -> {name, members}
    arrays = {}           # hex_idx -> {size, element_type}
    modifiers = {}        # hex_idx -> {referent, modifier}
    pointers = {}         # hex_idx -> {referent}

    # Current parse state
    current_fl_idx = None
    current_fl_members = []
    current_fl_enumerates = []
    current_struct_idx = None
    current_struct = None
    current_type_idx = None
    current_type_kind = None
    pending_lines = []  # buffer for multi-line record parsing

    i = 0
    while i < len(lines):
        line = lines[i]
        i += 1

        # Check for a new type record header
        m = re_type_header.match(line)
        if m:
            # Save previous fieldlist if any
            if current_fl_idx is not None:
                fieldlists[current_fl_idx] = {
                    'members': current_fl_members,
                    'enumerates': current_fl_enumerates,
                }
                current_fl_idx = None
                current_fl_members = []
                current_fl_enumerates = []

            idx = m.group(1).lower()
            kind = m.group(2)
            name = m.group(4) or ""

            if kind == "LF_STRUCTURE" or kind == "LF_CLASS":
                current_struct_idx = idx
                current_struct = {'name': name, 'sizeof': 0, 'field_list': None, 'forward_ref': None}
                # Parse continuation lines for field list and sizeof
                while i < len(lines) and not re_type_header.match(lines[i]) and not re_fieldlist_header.match(lines[i]):
                    subline = lines[i]
                    fm = re_forward_ref.search(subline)
                    if fm:
                        current_struct['forward_ref'] = fm.group(1).lower()
                    fl = re_field_list_ref.search(subline)
                    if fl:
                        fl_val = fl.group(1).lower()
                        if fl_val != "0x0000":  # <no type>
                            current_struct['field_list'] = fl_val
                    sz = re_sizeof.search(subline)
                    if sz:
                        current_struct['sizeof'] = int(sz.group(1))
                    i += 1

                if current_struct['forward_ref'] is None and current_struct['field_list']:
                    structs[idx] = current_struct
                continue

            elif kind == "LF_ENUM":
                # Parse the enum to find its field list
                enum_info = {'name': name, 'field_list': None}
                while i < len(lines) and not re_type_header.match(lines[i]) and not re_fieldlist_header.match(lines[i]):
                    subline = lines[i]
                    fl = re_field_list_ref.search(subline)
                    if fl:
                        fl_val = fl.group(1).lower()
                        if fl_val != "0x0000":
                            enum_info['field_list'] = fl_val
                    i += 1
                enums[idx] = enum_info
                continue

            elif kind == "LF_ARRAY":
                arr_info = {'size': 0, 'element_type': None}
                am = re_array.search(line)
                if am:
                    arr_info['size'] = int(am.group(1))
                    arr_info['element_type'] = am.group(2).lower()
                else:
                    while i < len(lines) and not re_type_header.match(lines[i]) and not re_fieldlist_header.match(lines[i]):
                        subline = lines[i]
                        am = re_array.search(subline)
                        if am:
                            arr_info['size'] = int(am.group(1))
                            arr_info['element_type'] = am.group(2).lower()
                        i += 1
                arrays[idx] = arr_info
                continue

            elif kind == "LF_MODIFIER":
                mm = re_modifier.search(line)
                if not mm:
                    # Check next line
                    if i < len(lines):
                        mm = re_modifier.search(lines[i])
                        i += 1
                if mm:
                    modifiers[idx] = {'referent': mm.group(1).lower(), 'modifier': mm.group(2)}
                continue

            elif kind == "LF_POINTER":
                pm = re_pointer.search(line)
                if not pm and i < len(lines):
                    pm = re_pointer.search(lines[i])
                    i += 1
                if pm:
                    pointers[idx] = {'referent': pm.group(1).lower()}
                continue

            else:
                # Skip other record types
                while i < len(lines) and not re_type_header.match(lines[i]) and not re_fieldlist_header.match(lines[i]):
                    i += 1
                continue

        # Check for fieldlist header
        fm = re_fieldlist_header.match(line)
        if fm:
            # Save previous fieldlist
            if current_fl_idx is not None:
                fieldlists[current_fl_idx] = {
                    'members': current_fl_members,
                    'enumerates': current_fl_enumerates,
                }

            current_fl_idx = fm.group(1).lower()
            current_fl_members = []
            current_fl_enumerates = []
            continue

        # Inside a fieldlist, collect members and enumerates
        if current_fl_idx is not None:
            em = re_enumerate.search(line)
            if em:
                current_fl_enumerates.append({
                    'name': em.group(1),
                    'value': int(em.group(2)),
                })
                continue

            mm = re_member.search(line)
            if mm:
                current_fl_members.append({
                    'name': mm.group(1),
                    'type_idx': mm.group(2).lower(),
                    'type_name': mm.group(3) or None,
                    'offset': int(mm.group(4)),
                })
                continue

            bm = re_bclass.search(line)
            if bm:
                current_fl_members.append({
                    'name': '__base',
                    'type_idx': bm.group(1).lower(),
                    'type_name': None,
                    'offset': int(bm.group(2)),
                    'is_base': True,
                })
                continue

    # Save last fieldlist
    if current_fl_idx is not None:
        fieldlists[current_fl_idx] = {
            'members': current_fl_members,
            'enumerates': current_fl_enumerates,
        }

    print(f"  Indexed: {len(fieldlists)} fieldlists, {len(structs)} structs, "
          f"{len(enums)} enums, {len(arrays)} arrays")

    return fieldlists, structs, enums, arrays, modifiers, pointers


def resolve_type(type_idx, structs, arrays, modifiers, pointers, depth=0):
    """Resolve a type index to a human-readable type description."""
    if depth > 10:
        return f"[{type_idx}]"

    idx_int = int(type_idx, 16)

    # Check builtin types
    if idx_int in BUILTIN_TYPES:
        return BUILTIN_TYPES[idx_int]

    # Check if it's a struct
    if type_idx in structs:
        return structs[type_idx]['name']

    # Check if it's an array
    if type_idx in arrays:
        arr = arrays[type_idx]
        elem_type = resolve_type(arr['element_type'], structs, arrays, modifiers, pointers, depth+1)
        elem_size = get_type_size(arr['element_type'], structs, arrays, modifiers, pointers)
        if elem_size > 0:
            count = arr['size'] // elem_size
            return f"{elem_type}[{count}]"
        return f"{elem_type}[?{arr['size']}B]"

    # Check modifier (const, volatile)
    if type_idx in modifiers:
        return resolve_type(modifiers[type_idx]['referent'], structs, arrays, modifiers, pointers, depth+1)

    # Check pointer
    if type_idx in pointers:
        ref = resolve_type(pointers[type_idx]['referent'], structs, arrays, modifiers, pointers, depth+1)
        return f"{ref}*"

    return f"[{type_idx}]"


def get_type_size(type_idx, structs, arrays, modifiers, pointers):
    """Get the size of a type in bytes."""
    idx_int = int(type_idx, 16)

    size_map = {
        0x0010: 1, 0x0020: 1, 0x0030: 1, 0x0070: 1,  # char/byte/bool
        0x0011: 2, 0x0021: 2, 0x0071: 2,  # short/ushort/wchar
        0x0012: 4, 0x0022: 4, 0x0074: 4, 0x0075: 4, 0x0040: 4,  # int/uint/float
        0x0013: 8, 0x0023: 8, 0x0041: 8,  # long long/double
        0x0403: 4, 0x0410: 4, 0x0474: 4,  # pointers (32-bit)
    }

    if idx_int in size_map:
        return size_map[idx_int]

    if type_idx in structs:
        return structs[type_idx]['sizeof']

    if type_idx in arrays:
        return arrays[type_idx]['size']

    if type_idx in modifiers:
        return get_type_size(modifiers[type_idx]['referent'], structs, arrays, modifiers, pointers)

    if type_idx in pointers:
        return 4  # 32-bit pointers

    return 0


def find_protocol_command_enum(fieldlists):
    """Find the PROTOCOL_COMMAND enum's fieldlist.

    It's the one containing NC_NULL=0, NC_LOG=1, NC_MISC=2 etc.
    """
    for fl_idx, fl in fieldlists.items():
        enums = fl['enumerates']
        if len(enums) < 10:
            continue

        names = {e['name']: e['value'] for e in enums}
        if names.get('NC_NULL') == 0 and names.get('NC_LOG') == 1 and names.get('NC_MISC') == 2:
            return fl_idx, enums

    return None, []


def classify_struct_department(struct_name, dept_names):
    """Find the longest matching department name for a struct."""
    stripped = struct_name.replace("PROTO_NC_", "")
    best = ""
    for dept in dept_names:
        if stripped.startswith(dept + "_") and len(dept) > len(best):
            best = dept
    return best or stripped.split("_")[0]


def resolve_struct_fields(struct, fieldlists, structs, arrays, modifiers, pointers):
    """Resolve all fields of a struct, including recursive type resolution."""
    fl_idx = struct.get('field_list')
    if not fl_idx or fl_idx not in fieldlists:
        return []

    fl = fieldlists[fl_idx]
    resolved = []

    for member in fl['members']:
        type_str = resolve_type(member['type_idx'], structs, arrays, modifiers, pointers)
        size = get_type_size(member['type_idx'], structs, arrays, modifiers, pointers)

        field = {
            'name': member['name'],
            'offset': member['offset'],
            'type': type_str,
            'type_idx': member['type_idx'],
            'size': size,
        }

        if member.get('is_base'):
            field['is_base'] = True

        resolved.append(field)

    return resolved


def collect_referenced_types(struct, fieldlists, structs, arrays, modifiers, pointers, visited=None):
    """Recursively collect all types referenced by a struct's fields."""
    if visited is None:
        visited = set()

    fl_idx = struct.get('field_list')
    if not fl_idx or fl_idx not in fieldlists:
        return {}

    referenced = {}
    fl = fieldlists[fl_idx]

    for member in fl['members']:
        type_idx = member['type_idx']
        if type_idx in visited:
            continue
        visited.add(type_idx)

        idx_int = int(type_idx, 16)
        if idx_int in BUILTIN_TYPES:
            continue

        # If it's a struct, include it and recurse
        if type_idx in structs:
            ref_struct = structs[type_idx]
            referenced[type_idx] = {
                'kind': 'struct',
                'name': ref_struct['name'],
                'sizeof': ref_struct['sizeof'],
                'fields': resolve_struct_fields(ref_struct, fieldlists, structs, arrays, modifiers, pointers),
            }
            sub_refs = collect_referenced_types(ref_struct, fieldlists, structs, arrays, modifiers, pointers, visited)
            referenced.update(sub_refs)

        elif type_idx in arrays:
            arr = arrays[type_idx]
            referenced[type_idx] = {
                'kind': 'array',
                'size': arr['size'],
                'element_type': resolve_type(arr['element_type'], structs, arrays, modifiers, pointers),
                'element_type_idx': arr['element_type'],
            }
            # Recurse into element type
            if arr['element_type'] in structs:
                visited.add(arr['element_type'])
                ref_struct = structs[arr['element_type']]
                referenced[arr['element_type']] = {
                    'kind': 'struct',
                    'name': ref_struct['name'],
                    'sizeof': ref_struct['sizeof'],
                    'fields': resolve_struct_fields(ref_struct, fieldlists, structs, arrays, modifiers, pointers),
                }
                sub_refs = collect_referenced_types(ref_struct, fieldlists, structs, arrays, modifiers, pointers, visited)
                referenced.update(sub_refs)

    return referenced


def main():
    if len(sys.argv) < 3:
        print("Usage: python3 extract-pdb-protocol.py <pdb-dump-file> <output-dir>")
        print()
        print("Example:")
        print("  python3 tools/extract-pdb-protocol.py ~/test.txt docs/extracted/worldmanager")
        sys.exit(1)

    input_file = sys.argv[1]
    output_dir = sys.argv[2]

    if not os.path.isfile(input_file):
        print(f"Error: Input file not found: {input_file}")
        sys.exit(1)

    os.makedirs(os.path.join(output_dir, "enums"), exist_ok=True)
    os.makedirs(os.path.join(output_dir, "structs"), exist_ok=True)

    print(f"=== PDB Protocol Extractor ===")
    print(f"Input:  {input_file}")
    print(f"Output: {output_dir}")
    print()

    # --- Parse ---
    fieldlists, structs, enums, arrays, modifiers, pointers = parse_pdb_dump(input_file)

    # --- Find PROTOCOL_COMMAND department table ---
    print()
    print("--- Finding PROTOCOL_COMMAND department table ---")
    pc_fl_idx, pc_enums = find_protocol_command_enum(fieldlists)

    if not pc_enums:
        print("ERROR: Could not find PROTOCOL_COMMAND enum!")
        print("Falling back to heuristic detection...")
        sys.exit(1)

    print(f"  Found at fieldlist {pc_fl_idx}: {len(pc_enums)} departments")

    # Build department map: name -> id
    departments = {}
    for e in pc_enums:
        departments[e['name'].replace('NC_', '')] = e['value']

    dept_names = sorted(departments.keys(), key=lambda k: departments[k])

    # Write department IDs
    with open(os.path.join(output_dir, "department-ids.txt"), 'w') as f:
        for name in dept_names:
            f.write(f"NC_{name} = {departments[name]}\n")

    for name in dept_names:
        print(f"  0x{departments[name]:02X} {name}")

    # --- Collect all per-department enum values ---
    print()
    print("--- Collecting per-department enum values ---")

    # Find all NC_* enumerate values across all fieldlists
    all_enums = {}  # name -> value
    for fl_idx, fl in fieldlists.items():
        for e in fl['enumerates']:
            if e['name'].startswith('NC_') and '_' in e['name'][3:]:
                all_enums[e['name']] = e['value']

    print(f"  {len(all_enums)} total opcode enum values")

    # Group by department (longest match)
    dept_enums = defaultdict(list)  # dept_name -> [(name, value)]

    for enum_name, enum_val in all_enums.items():
        stripped = enum_name[3:]  # remove NC_
        best_dept = ""
        for dept in dept_names:
            if stripped.startswith(dept + "_") and len(dept) > len(best_dept):
                best_dept = dept
        if best_dept:
            dept_enums[best_dept].append((enum_name, enum_val))
        else:
            dept_enums["_UNCATEGORIZED"].append((enum_name, enum_val))

    # Write per-department enum files
    for dept in dept_names:
        if dept not in dept_enums:
            continue
        entries = sorted(dept_enums[dept], key=lambda x: x[1])
        hex_id = f"0x{departments[dept]:02X}"
        filepath = os.path.join(output_dir, "enums", f"{dept}.txt")
        with open(filepath, 'w') as f:
            f.write(f"# {dept} ({hex_id}) - {len(entries)} opcodes\n")
            f.write(f"# Full opcode = {hex_id} << 8 | cmd_value\n")
            f.write(f"#\n")
            for name, val in entries:
                f.write(f"{name} = {val}\n")
        print(f"  {dept} ({hex_id}): {len(entries)} opcodes")

    if "_UNCATEGORIZED" in dept_enums:
        entries = sorted(dept_enums["_UNCATEGORIZED"], key=lambda x: x[1])
        filepath = os.path.join(output_dir, "enums", "_UNCATEGORIZED.txt")
        with open(filepath, 'w') as f:
            for name, val in entries:
                f.write(f"{name} = {val}\n")
        print(f"  _UNCATEGORIZED: {len(entries)} opcodes")

    # Write all-enums.json
    enums_json = {}
    for dept in dept_names:
        if dept not in dept_enums:
            continue
        enums_json[dept] = {
            'id': departments[dept],
            'hex': f"0x{departments[dept]:02X}",
            'opcodes': {name: val for name, val in sorted(dept_enums[dept], key=lambda x: x[1])},
        }

    with open(os.path.join(output_dir, "all-enums.json"), 'w') as f:
        json.dump(enums_json, f, indent=2)

    # --- Collect all PROTO_NC_* structs ---
    print()
    print("--- Collecting protocol structs ---")

    proto_structs = {}  # name -> struct info
    dept_structs = defaultdict(list)  # dept_name -> [struct_info]

    for idx, s in structs.items():
        name = s['name']
        if not name.startswith('PROTO_NC_'):
            continue
        if '::' in name:
            # Skip nested/scoped types (e.g. CGuildManager::Send_XXX::__l2::PROTO_XXX_SEND)
            continue

        dept = classify_struct_department(name, dept_names)
        fields = resolve_struct_fields(s, fieldlists, structs, arrays, modifiers, pointers)

        struct_info = {
            'name': name,
            'sizeof': s['sizeof'],
            'department': dept,
            'type_idx': idx,
            'fields': fields,
        }

        # Avoid duplicates (forward ref vs definition)
        if name not in proto_structs or s['sizeof'] > 0:
            proto_structs[name] = struct_info
            dept_structs[dept].append(struct_info)

    # Deduplicate dept_structs (same name can appear from multiple type indices)
    for dept in dept_structs:
        seen = set()
        deduped = []
        for s in dept_structs[dept]:
            if s['name'] not in seen:
                seen.add(s['name'])
                deduped.append(s)
        dept_structs[dept] = sorted(deduped, key=lambda x: x['name'])

    print(f"  {len(proto_structs)} unique PROTO_NC_* structs")

    # Write per-department struct files
    for dept in dept_names:
        if dept not in dept_structs:
            continue
        filepath = os.path.join(output_dir, "structs", f"{dept}.txt")
        with open(filepath, 'w') as f:
            for s in dept_structs[dept]:
                f.write(f"## {s['name']} (sizeof {s['sizeof']})\n")
                if s['fields']:
                    for field in s['fields']:
                        prefix = "  base " if field.get('is_base') else "  "
                        f.write(f"{prefix}+{field['offset']}: {field['type']} {field['name']}\n")
                else:
                    f.write(f"  (no fields)\n")
                f.write(f"\n")
        print(f"  {dept}: {len(dept_structs[dept])} structs")

    # Write all-structs.json (with resolved fields for code generation)
    structs_json = {}
    for name, s in sorted(proto_structs.items()):
        structs_json[name] = {
            'sizeof': s['sizeof'],
            'department': s['department'],
            'fields': [
                {
                    'name': f['name'],
                    'offset': f['offset'],
                    'type': f['type'],
                    'type_idx': f['type_idx'],
                    'size': f['size'],
                    **({"is_base": True} if f.get('is_base') else {}),
                }
                for f in s['fields']
            ],
        }

    with open(os.path.join(output_dir, "all-structs.json"), 'w') as f:
        json.dump(structs_json, f, indent=2)

    # --- Generate department index ---
    print()
    print("--- Generating department index ---")

    with open(os.path.join(output_dir, "department-index.md"), 'w') as f:
        f.write("# Protocol Department Index\n\n")
        f.write(f"Generated from: {os.path.basename(input_file)}\n\n")
        f.write("| ID | Hex | Department | Enums | Structs |\n")
        f.write("|----|-----|-----------|-------|--------|\n")

        total_enums = 0
        total_structs = 0

        for dept in dept_names:
            hex_id = f"0x{departments[dept]:02X}"
            enum_count = len(dept_enums.get(dept, []))
            struct_count = len(dept_structs.get(dept, []))
            total_enums += enum_count
            total_structs += struct_count
            f.write(f"| {departments[dept]} | {hex_id} | {dept} | {enum_count} | {struct_count} |\n")

        f.write(f"\n**Totals:** {total_enums} enum values, {total_structs} struct definitions\n")

    print()
    print(f"=== Done ===")
    print(f"Output: {output_dir}")
    print(f"  department-index.md  - {len(dept_names)} departments")
    print(f"  enums/               - {sum(1 for d in dept_names if d in dept_enums)} department files")
    print(f"  structs/             - {sum(1 for d in dept_names if d in dept_structs)} department files")
    print(f"  all-enums.json       - {len(all_enums)} enum values (JSON)")
    print(f"  all-structs.json     - {len(proto_structs)} struct definitions (JSON)")


if __name__ == '__main__':
    main()
