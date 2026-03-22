#!/bin/bash
# extract-pdb-protocol.sh - Extract all protocol enums and structs from a PDB type dump
#
# Usage:
#   ./tools/extract-pdb-protocol.sh <pdb-dump-file> <output-dir>
#
# The input file should be created with:
#   llvm-pdbutil dump -types <file.pdb> > dump.txt
#
# Output:
#   <output-dir>/department-index.md   - Department ID table
#   <output-dir>/enums/                - One file per department with all opcode enum values
#   <output-dir>/structs/              - One file per department with all struct definitions
#   <output-dir>/all-enums.txt         - Flat list of all enum values (for tooling)
#   <output-dir>/all-structs.txt       - Flat list of all struct names with sizes (for tooling)
#
# Notes:
#   - If the input file is UTF-16 (as produced by Windows cmd redirection),
#     null bytes are stripped automatically.
#   - This script is idempotent: re-running it overwrites previous output.

set -uo pipefail

if [ $# -lt 2 ]; then
    echo "Usage: $0 <pdb-dump-file> <output-dir>"
    echo ""
    echo "Example:"
    echo "  $0 ~/test.txt docs/extracted/worldmanager"
    exit 1
fi

INPUT_FILE="$1"
OUTPUT_DIR="$2"

if [ ! -f "$INPUT_FILE" ]; then
    echo "Error: Input file not found: $INPUT_FILE"
    exit 1
fi

# Create output directories
mkdir -p "$OUTPUT_DIR/enums" "$OUTPUT_DIR/structs"

echo "=== PDB Protocol Extractor ==="
echo "Input:  $INPUT_FILE"
echo "Output: $OUTPUT_DIR"

# --- Step 0: Prepare clean input (strip UTF-16 null bytes if present) ---
CLEAN_FILE=$(mktemp)
trap "rm -f $CLEAN_FILE" EXIT

# Always strip null bytes - safe for both UTF-16 and ASCII
cat "$INPUT_FILE" | tr -d '\0' | tr -d '\r' > "$CLEAN_FILE"

TOTAL_LINES=$(wc -l < "$CLEAN_FILE")
echo "Cleaned input: $TOTAL_LINES lines"

# --- Step 1: Extract ALL enum values ---
echo ""
echo "--- Extracting all enum values ---"

grep -o "LF_ENUMERATE \[NC_[A-Za-z0-9_]* = [0-9]*\]" "$CLEAN_FILE" \
    | sed 's/LF_ENUMERATE \[//' | sed 's/\]//' \
    | sort -u \
    > "$OUTPUT_DIR/all-enums.txt"

TOTAL_ENUMS=$(wc -l < "$OUTPUT_DIR/all-enums.txt" | tr -d ' ')
echo "Found $TOTAL_ENUMS unique enum values"

# --- Step 2: Extract department header IDs from PROTOCOL_COMMAND enum ---
echo ""
echo "--- Extracting department IDs ---"

# Find the PROTOCOL_COMMAND enum's LF_FIELDLIST. This is the authoritative
# department table. It's the fieldlist containing entries like:
#   LF_ENUMERATE [NC_NULL = 0]
#   LF_ENUMERATE [NC_LOG = 1]
#   ...
# We find it by looking for a fieldlist that contains NC_NULL = 0 followed
# by NC_LOG = 1 (unique to the PROTOCOL_COMMAND enum, not any per-dept enum).
awk '
/LF_FIELDLIST/ { in_fl = 1; has_null = 0; has_log = 0; buf = ""; next }
in_fl && /LF_ENUMERATE \[NC_NULL = 0\]/ { has_null = 1 }
in_fl && /LF_ENUMERATE \[NC_LOG = 1\]/ { has_log = 1 }
in_fl && /LF_ENUMERATE/ {
    # Extract "NC_FOO = N" from the line
    match($0, /NC_[A-Z_]+ = [0-9]+/)
    if (RSTART > 0) {
        buf = buf substr($0, RSTART, RLENGTH) "\n"
    }
}
in_fl && /^[[:space:]]*0x[0-9a-fA-F]+ \|/ && !/LF_FIELDLIST/ {
    if (has_null && has_log) {
        printf "%s", buf
        exit
    }
    in_fl = 0; buf = ""
}
' "$CLEAN_FILE" | sort -t= -k2 -n > "$OUTPUT_DIR/department-ids.txt"

echo "Found $(wc -l < "$OUTPUT_DIR/department-ids.txt" | tr -d ' ') departments"
echo "Departments:"
cat "$OUTPUT_DIR/department-ids.txt"

# --- Step 3: Group enums by department ---
echo ""
echo "--- Grouping enums by department ---"

declare -A DEPT_HEX
declare -a DEPT_NAMES=()
while IFS='=' read -r name val; do
    name=$(echo "$name" | sed 's/NC_//' | tr -d ' ')
    val=$(echo "$val" | tr -d ' ')
    DEPT_HEX["$name"]=$(printf "0x%02X" "$val")
    DEPT_NAMES+=("$name")
done < "$OUTPUT_DIR/department-ids.txt"

# Write per-department enum files
for dept_name in "${DEPT_NAMES[@]}"; do
    hex="${DEPT_HEX[$dept_name]}"
    pattern="^NC_${dept_name}_"
    count=$(grep -c "$pattern" "$OUTPUT_DIR/all-enums.txt" || true)
    count=$(echo "$count" | tr -d ' ')

    if [ "$count" -gt 0 ] 2>/dev/null; then
        {
            echo "# ${dept_name} (${hex}) - ${count} opcodes"
            echo "# Full opcode = ${hex} << 8 | cmd_value"
            echo "#"
            grep "$pattern" "$OUTPUT_DIR/all-enums.txt" | sort -t= -k2 -n
        } > "$OUTPUT_DIR/enums/${dept_name}.txt"
        echo "  $dept_name ($hex): $count opcodes"
    fi
done

# --- Step 4: Extract ALL struct definitions ---
echo ""
echo "--- Extracting struct definitions ---"

# Use awk to properly parse multi-line LF_STRUCTURE records
# We need: struct name, sizeof, and field list index
awk '
/LF_STRUCTURE.*`PROTO_NC_/ {
    # Extract struct name
    match($0, /`PROTO_NC_[A-Za-z0-9_]*`/)
    name = substr($0, RSTART+1, RLENGTH-2)
    getline; # vtable/unique name line or options
    # Read until we find field list and sizeof (up to 5 lines)
    fl = ""; sz = ""
    for (i = 0; i < 5; i++) {
        if ($0 ~ /forward ref/) { name = ""; break }
        if (match($0, /field list: 0x[0-9a-fA-F]+/)) {
            fl = substr($0, RSTART+12, RLENGTH-12)
        }
        if (match($0, /sizeof [0-9]+/)) {
            sz = substr($0, RSTART+7, RLENGTH-7)
        }
        if (fl != "" && sz != "") break
        getline
    }
    if (name != "" && fl != "" && sz != "") {
        print name "|" sz "|" fl
    }
}
' "$CLEAN_FILE" | sort -u > "$OUTPUT_DIR/all-structs.txt"

TOTAL_STRUCTS=$(wc -l < "$OUTPUT_DIR/all-structs.txt" | tr -d ' ')
echo "Found $TOTAL_STRUCTS unique struct definitions"

# --- Step 5: Extract field members for each struct ---
echo ""
echo "--- Extracting struct fields ---"

# Clear existing struct files
rm -f "$OUTPUT_DIR/structs/"*.txt

# Pre-extract all LF_FIELDLIST blocks with their members into a temp file
# Format: FL_IDX|MEMBER_LINE
awk '
/^[[:space:]]*0x[0-9a-fA-F]+ \| LF_FIELDLIST/ {
    match($0, /0x[0-9a-fA-F]+/)
    fl_idx = substr($0, RSTART, RLENGTH)
    next
}
/LF_MEMBER/ && fl_idx != "" {
    print fl_idx "|" $0
}
/LF_BCLASS/ && fl_idx != "" {
    print fl_idx "|" $0
}
/^[[:space:]]*0x[0-9a-fA-F]+ \|/ && !/LF_FIELDLIST/ {
    fl_idx = ""
}
' "$CLEAN_FILE" > /tmp/pdb-fieldlists.txt

echo "Extracted $(wc -l < /tmp/pdb-fieldlists.txt | tr -d ' ') field entries"

# Now process each struct
while IFS='|' read -r struct_name struct_size fl_idx; do
    # Determine department by finding the longest matching department name prefix
    # Sort department names by length (longest first) so compound names match first
    dept=""
    best_len=0
    stripped=$(echo "$struct_name" | sed 's/PROTO_NC_//')
    for known_dept in "${DEPT_NAMES[@]}"; do
        if echo "$struct_name" | grep -q "^PROTO_NC_${known_dept}_"; then
            dept_len=${#known_dept}
            if [ "$dept_len" -gt "$best_len" ]; then
                dept="$known_dept"
                best_len=$dept_len
            fi
        fi
    done
    if [ -z "$dept" ]; then
        # Fallback: extract first word after PROTO_NC_
        dept=$(echo "$stripped" | sed 's/_[A-Z0-9].*$//')
    fi

    struct_file="$OUTPUT_DIR/structs/${dept}.txt"

    # Get fields from pre-extracted fieldlist data
    fields=$(grep "^${fl_idx}|" /tmp/pdb-fieldlists.txt || true)

    {
        echo "## ${struct_name} (sizeof ${struct_size})"
        if [ -n "$fields" ]; then
            echo "$fields" | while IFS='|' read -r _ field_line; do
                if echo "$field_line" | grep -q "LF_MEMBER"; then
                    fname=$(echo "$field_line" | sed "s/.*name = \`//" | sed "s/\`.*//")
                    ftype_name=$(echo "$field_line" | sed 's/.*(\([^)]*\)).*/\1/' || true)
                    foffset=$(echo "$field_line" | sed 's/.*offset = //' | sed 's/,.*//')

                    # Check if type name was extracted (it's in parentheses after hex)
                    if echo "$field_line" | grep -q "Type = 0x[0-9a-fA-F]* ("; then
                        echo "  +${foffset}: ${ftype_name} ${fname}"
                    else
                        ftype_hex=$(echo "$field_line" | sed 's/.*Type = //' | sed 's/,.*//')
                        echo "  +${foffset}: [${ftype_hex}] ${fname}"
                    fi
                elif echo "$field_line" | grep -q "LF_BCLASS"; then
                    btype=$(echo "$field_line" | sed 's/.*type = //' | sed 's/,.*//')
                    boffset=$(echo "$field_line" | sed 's/.*offset = //' | sed 's/,.*//')
                    echo "  +${boffset}: [base ${btype}]"
                fi
            done
        else
            echo "  (no fields - empty struct or complex type)"
        fi
        echo ""
    } >> "$struct_file"

done < "$OUTPUT_DIR/all-structs.txt"

STRUCT_FILES=$(ls "$OUTPUT_DIR/structs/" 2>/dev/null | wc -l | tr -d ' ')
echo "Generated $STRUCT_FILES department struct files"

# --- Step 6: Generate department index ---
echo ""
echo "--- Generating department index ---"

{
    echo "# Protocol Department Index"
    echo ""
    echo "Generated from: $(basename "$INPUT_FILE")"
    echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date +%Y-%m-%d)"
    echo ""
    echo "| ID | Department | Enums | Structs |"
    echo "|----|-----------|-------|---------|"

    while IFS='=' read -r name val; do
        name=$(echo "$name" | sed 's/NC_//' | tr -d ' ')
        val=$(echo "$val" | tr -d ' ')
        hex=$(printf "0x%02X" "$val")

        enum_count=0
        if [ -f "$OUTPUT_DIR/enums/${name}.txt" ]; then
            enum_count=$(grep -c "^NC_" "$OUTPUT_DIR/enums/${name}.txt" || true)
            enum_count=$(echo "$enum_count" | tr -d ' ')
        fi

        struct_count=0
        if [ -f "$OUTPUT_DIR/structs/${name}.txt" ]; then
            struct_count=$(grep -c "^## PROTO_" "$OUTPUT_DIR/structs/${name}.txt" || true)
            struct_count=$(echo "$struct_count" | tr -d ' ')
        fi

        printf "| %s | %s | %d | %d |\n" "$hex" "$name" "$enum_count" "$struct_count"
    done < "$OUTPUT_DIR/department-ids.txt"

    echo ""
    echo "### Totals"
    echo ""
    echo "- **$TOTAL_ENUMS** unique enum values across all departments"
    echo "- **$TOTAL_STRUCTS** unique struct definitions"

} > "$OUTPUT_DIR/department-index.md"

# Cleanup temp
rm -f /tmp/pdb-fieldlists.txt

echo ""
echo "=== Done ==="
echo "Output: $OUTPUT_DIR"
echo "  department-index.md  - Summary table"
echo "  all-enums.txt        - $TOTAL_ENUMS enum values"
echo "  all-structs.txt      - $TOTAL_STRUCTS struct defs (name|size|fieldlist_idx)"
echo "  enums/               - Per-department enum files"
echo "  structs/             - Per-department struct files"
