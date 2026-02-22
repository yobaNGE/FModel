using System.Collections.Generic;

namespace FModel.ViewModels.LowLevel;

public enum LowLevelOffsetDisplayMode
{
    Raw,
    Parser,
    Script
}

public enum LowLevelOffsetConfidence
{
    Exact,
    Derived,
    Fallback
}

public enum LowLevelRawRangeSource
{
    ParserCaptured,
    FallbackDerived
}

public enum LowLevelOpcodeCategory
{
    Flow,
    Call,
    Variable,
    Constant,
    Container,
    Delegate,
    Cast,
    Context,
    Instrumentation,
    Transaction,
    Other
}

public enum LowLevelOperandConfidence
{
    Exact,
    Derived,
    Fallback
}

public enum LowLevelCfgEdgeKind
{
    Fallthrough,
    Jump,
    ConditionalTrue,
    ConditionalFalse,
    ExecutionFlow,
    Unknown
}

public enum LowLevelXrefKind
{
    CallFinal,
    CallVirtual,
    ObjectRef,
    PropertyRef,
    FieldPathRef,
    Other
}

public sealed class LowLevelPackageData
{
    public string PackageName { get; }
    public string PackageType { get; }
    public string? Warning { get; }
    public IReadOnlyList<LowLevelClassData> Classes { get; }
    public IReadOnlyList<LowLevelExportMapEntryData> ExportMapEntries { get; }
    public IReadOnlyList<LowLevelImportMapEntryData> ImportMapEntries { get; }
    public IReadOnlyList<LowLevelNameMapEntryData> NameMapEntries { get; }

    public LowLevelPackageData(
        string packageName,
        string packageType,
        IReadOnlyList<LowLevelClassData> classes,
        string? warning = null,
        IReadOnlyList<LowLevelExportMapEntryData>? exportMapEntries = null,
        IReadOnlyList<LowLevelImportMapEntryData>? importMapEntries = null,
        IReadOnlyList<LowLevelNameMapEntryData>? nameMapEntries = null)
    {
        PackageName = packageName;
        PackageType = packageType;
        Warning = warning;
        Classes = classes;
        ExportMapEntries = exportMapEntries ?? [];
        ImportMapEntries = importMapEntries ?? [];
        NameMapEntries = nameMapEntries ?? [];
    }
}

public sealed class LowLevelClassData
{
    public int ExportIndex { get; }
    public string ClassName { get; }
    public string BaseClassName { get; }
    public IReadOnlyList<LowLevelFunctionData> Functions { get; }
    public IReadOnlyList<LowLevelPropertyTagData> PropertyTags { get; }
    public string? Warning { get; }
    public string ClassKey => $"{ClassName}#{ExportIndex}";

    public LowLevelClassData(
        int exportIndex,
        string className,
        string baseClassName,
        IReadOnlyList<LowLevelFunctionData> functions,
        IReadOnlyList<LowLevelPropertyTagData> propertyTags,
        string? warning = null)
    {
        ExportIndex = exportIndex;
        ClassName = className;
        BaseClassName = baseClassName;
        Functions = functions;
        PropertyTags = propertyTags;
        Warning = warning;
    }
}

public sealed class LowLevelFunctionData
{
    public int ExportIndex { get; }
    public string Name { get; }
    public string OwnerClassName { get; }
    public string Key { get; }
    public string Flags { get; }
    public string ScriptOffsetSummary { get; }
    public int ScriptSize { get; }
    public long? RawToParserDelta { get; }
    public IReadOnlyList<LowLevelInstructionData> Instructions { get; }
    public IReadOnlyList<LowLevelHexRowData> HexRows { get; }
    public IReadOnlyList<LowLevelBasicBlockData> BasicBlocks { get; }
    public IReadOnlyList<LowLevelCfgEdgeData> CfgEdges { get; }
    public IReadOnlyList<LowLevelXrefData> OutgoingXrefs { get; }
    public IReadOnlyList<LowLevelXrefData> IncomingXrefs { get; }
    public string? Warning { get; }

    public int InstructionCount => Instructions.Count;

    public LowLevelFunctionData(
        int exportIndex,
        string name,
        string ownerClassName,
        string key,
        string flags,
        string scriptOffsetSummary,
        int scriptSize,
        long? rawToParserDelta,
        IReadOnlyList<LowLevelInstructionData> instructions,
        IReadOnlyList<LowLevelHexRowData> hexRows,
        IReadOnlyList<LowLevelBasicBlockData>? basicBlocks = null,
        IReadOnlyList<LowLevelCfgEdgeData>? cfgEdges = null,
        IReadOnlyList<LowLevelXrefData>? outgoingXrefs = null,
        IReadOnlyList<LowLevelXrefData>? incomingXrefs = null,
        string? warning = null)
    {
        ExportIndex = exportIndex;
        Name = name;
        OwnerClassName = ownerClassName;
        Key = key;
        Flags = flags;
        ScriptOffsetSummary = scriptOffsetSummary;
        ScriptSize = scriptSize;
        RawToParserDelta = rawToParserDelta;
        Instructions = instructions;
        HexRows = hexRows;
        BasicBlocks = basicBlocks ?? [];
        CfgEdges = cfgEdges ?? [];
        OutgoingXrefs = outgoingXrefs ?? [];
        IncomingXrefs = incomingXrefs ?? [];
        Warning = warning;
    }

    public LowLevelFunctionData WithIncomingXrefs(IReadOnlyList<LowLevelXrefData> incomingXrefs)
    {
        return new LowLevelFunctionData(
            ExportIndex,
            Name,
            OwnerClassName,
            Key,
            Flags,
            ScriptOffsetSummary,
            ScriptSize,
            RawToParserDelta,
            Instructions,
            HexRows,
            BasicBlocks,
            CfgEdges,
            OutgoingXrefs,
            incomingXrefs,
            Warning);
    }

    public LowLevelFunctionData WithXrefs(IReadOnlyList<LowLevelXrefData> outgoingXrefs, IReadOnlyList<LowLevelXrefData> incomingXrefs)
    {
        return new LowLevelFunctionData(
            ExportIndex,
            Name,
            OwnerClassName,
            Key,
            Flags,
            ScriptOffsetSummary,
            ScriptSize,
            RawToParserDelta,
            Instructions,
            HexRows,
            BasicBlocks,
            CfgEdges,
            outgoingXrefs,
            incomingXrefs,
            Warning);
    }
}

public sealed class LowLevelInstructionData
{
    public int Index { get; }
    public long RawOffset { get; }
    public long ParserOffset { get; }
    public int ScriptOffset { get; }
    public long DeltaToParser { get; }
    public LowLevelOffsetConfidence OffsetConfidence { get; }
    public int BasicBlockId { get; }
    public string Opcode { get; }
    public byte OpcodeByte { get; }
    public LowLevelOpcodeCategory OpcodeCategory { get; }
    public string Bytes { get; }
    public string Decoded { get; }
    public IReadOnlyList<LowLevelOperandNodeData> Operands { get; }
    public string Label { get; }
    public int? JumpTargetScriptOffset { get; }
    public int Length { get; }
    public int RawByteStart { get; }
    public int RawByteEnd { get; }
    public LowLevelRawRangeSource RawRangeSource { get; }
    public bool RawRangeValid { get; }
    public string JumpTargetLabel => JumpTargetScriptOffset.HasValue ? $"Label_0x{JumpTargetScriptOffset.Value:X4}" : string.Empty;
    public string RawOffsetDisplay => $"0x{RawOffset:X8}";
    public string ParserOffsetDisplay => $"0x{ParserOffset:X8}";
    public string ScriptOffsetDisplay => $"0x{ScriptOffset:X4}";
    public string RawByteRangeDisplay => $"0x{RawByteStart:X4}..0x{RawByteEnd:X4}";

    public LowLevelInstructionData(
        int index,
        long rawOffset,
        long parserOffset,
        int scriptOffset,
        long deltaToParser,
        LowLevelOffsetConfidence offsetConfidence,
        int basicBlockId,
        string opcode,
        byte opcodeByte,
        LowLevelOpcodeCategory opcodeCategory,
        string bytes,
        string decoded,
        IReadOnlyList<LowLevelOperandNodeData> operands,
        string label,
        int? jumpTargetScriptOffset,
        int length,
        int rawByteStart,
        int rawByteEnd,
        LowLevelRawRangeSource rawRangeSource,
        bool rawRangeValid)
    {
        Index = index;
        RawOffset = rawOffset;
        ParserOffset = parserOffset;
        ScriptOffset = scriptOffset;
        DeltaToParser = deltaToParser;
        OffsetConfidence = offsetConfidence;
        BasicBlockId = basicBlockId;
        Opcode = opcode;
        OpcodeByte = opcodeByte;
        OpcodeCategory = opcodeCategory;
        Bytes = bytes;
        Decoded = decoded;
        Operands = operands;
        Label = label;
        JumpTargetScriptOffset = jumpTargetScriptOffset;
        Length = length;
        RawByteStart = rawByteStart;
        RawByteEnd = rawByteEnd;
        RawRangeSource = rawRangeSource;
        RawRangeValid = rawRangeValid;
    }
}

public sealed class LowLevelOperandNodeData
{
    public string Name { get; }
    public string Kind { get; }
    public string Type { get; }
    public string Value { get; }
    public string Bytes { get; }
    public int Start { get; }
    public int End { get; }
    public LowLevelOperandConfidence Confidence { get; }
    public string? Reason { get; }
    public IReadOnlyList<LowLevelOperandNodeData> Children { get; }

    public bool HasRange => Start >= 0 && End > Start;
    public int Length => HasRange ? End - Start : 0;
    public string RangeDisplay => HasRange ? $"0x{Start:X4}..0x{End:X4}" : "<unknown>";

    public LowLevelOperandNodeData(
        string name,
        string kind,
        string type,
        string value,
        string bytes,
        int start,
        int end,
        LowLevelOperandConfidence confidence,
        string? reason = null,
        IReadOnlyList<LowLevelOperandNodeData>? children = null)
    {
        Name = name;
        Kind = kind;
        Type = type;
        Value = value;
        Bytes = bytes;
        Start = start;
        End = end;
        Confidence = confidence;
        Reason = reason;
        Children = children ?? [];
    }
}

public sealed class LowLevelPropertyTagData
{
    public string Scope { get; }
    public string Name { get; }
    public string Type { get; }
    public string Value { get; }
    public long StartOffset { get; }
    public long EndOffset { get; }
    public long ValueOffset { get; }
    public bool ParserFallback { get; }
    public string RangeDisplay => $"0x{StartOffset:X8}..0x{EndOffset:X8}";
    public string ValueOffsetDisplay => $"0x{ValueOffset:X8}";

    public LowLevelPropertyTagData(
        string scope,
        string name,
        string type,
        string value,
        long startOffset,
        long endOffset,
        long valueOffset,
        bool parserFallback)
    {
        Scope = scope;
        Name = name;
        Type = type;
        Value = value;
        StartOffset = startOffset;
        EndOffset = endOffset;
        ValueOffset = valueOffset;
        ParserFallback = parserFallback;
    }
}

public sealed class LowLevelHexRowData
{
    public long Offset { get; }
    public string Hex { get; }
    public string Ascii { get; }

    public LowLevelHexRowData(long offset, string hex, string ascii)
    {
        Offset = offset;
        Hex = hex;
        Ascii = ascii;
    }
}

public sealed class LowLevelBasicBlockData
{
    public int Id { get; }
    public int StartScriptOffset { get; }
    public int EndScriptOffset { get; }
    public int FirstInstructionIndex { get; }
    public int LastInstructionIndex { get; }
    public string Summary { get; }
    public string ScriptRangeDisplay => $"0x{StartScriptOffset:X4}..0x{EndScriptOffset:X4}";

    public LowLevelBasicBlockData(
        int id,
        int startScriptOffset,
        int endScriptOffset,
        int firstInstructionIndex,
        int lastInstructionIndex,
        string summary)
    {
        Id = id;
        StartScriptOffset = startScriptOffset;
        EndScriptOffset = endScriptOffset;
        FirstInstructionIndex = firstInstructionIndex;
        LastInstructionIndex = lastInstructionIndex;
        Summary = summary;
    }
}

public sealed class LowLevelCfgEdgeData
{
    public int FromBlockId { get; }
    public int ToBlockId { get; }
    public LowLevelCfgEdgeKind Kind { get; }
    public string Label { get; }

    public LowLevelCfgEdgeData(int fromBlockId, int toBlockId, LowLevelCfgEdgeKind kind, string label)
    {
        FromBlockId = fromBlockId;
        ToBlockId = toBlockId;
        Kind = kind;
        Label = label;
    }
}

public sealed class LowLevelXrefData
{
    public LowLevelXrefKind Kind { get; }
    public string SourceFunctionKey { get; }
    public string SourceFunctionName { get; }
    public int SourceInstructionIndex { get; }
    public int SourceScriptOffset { get; }
    public long SourceRawOffset { get; }
    public long SourceParserOffset { get; }
    public string TargetKind { get; }
    public string TargetKey { get; }
    public string TargetDisplay { get; }
    public string? TargetFunctionKey { get; }
    public bool IsResolved { get; }
    public string DirectionLabel { get; }
    public string SourceLocationDisplay => $"{SourceFunctionName} @0x{SourceScriptOffset:X4}";

    public LowLevelXrefData(
        LowLevelXrefKind kind,
        string sourceFunctionKey,
        string sourceFunctionName,
        int sourceInstructionIndex,
        int sourceScriptOffset,
        long sourceRawOffset,
        long sourceParserOffset,
        string targetKind,
        string targetKey,
        string targetDisplay,
        string? targetFunctionKey,
        bool isResolved,
        string directionLabel)
    {
        Kind = kind;
        SourceFunctionKey = sourceFunctionKey;
        SourceFunctionName = sourceFunctionName;
        SourceInstructionIndex = sourceInstructionIndex;
        SourceScriptOffset = sourceScriptOffset;
        SourceRawOffset = sourceRawOffset;
        SourceParserOffset = sourceParserOffset;
        TargetKind = targetKind;
        TargetKey = targetKey;
        TargetDisplay = targetDisplay;
        TargetFunctionKey = targetFunctionKey;
        IsResolved = isResolved;
        DirectionLabel = directionLabel;
    }

    public LowLevelXrefData WithDirection(string directionLabel)
    {
        return new LowLevelXrefData(
            Kind,
            SourceFunctionKey,
            SourceFunctionName,
            SourceInstructionIndex,
            SourceScriptOffset,
            SourceRawOffset,
            SourceParserOffset,
            TargetKind,
            TargetKey,
            TargetDisplay,
            TargetFunctionKey,
            IsResolved,
            directionLabel);
    }
}

public sealed class LowLevelExportMapEntryData
{
    public int Index { get; }
    public string ObjectName { get; }
    public string ClassName { get; }
    public string OuterName { get; }
    public long RawOffset { get; }
    public long ParserOffset { get; }
    public long Size { get; }
    public string Flags { get; }
    public string RawOffsetDisplay => $"0x{RawOffset:X8}";
    public string ParserOffsetDisplay => $"0x{ParserOffset:X8}";
    public string SizeDisplay => $"0x{Size:X}";

    public LowLevelExportMapEntryData(
        int index,
        string objectName,
        string className,
        string outerName,
        long rawOffset,
        long parserOffset,
        long size,
        string flags)
    {
        Index = index;
        ObjectName = objectName;
        ClassName = className;
        OuterName = outerName;
        RawOffset = rawOffset;
        ParserOffset = parserOffset;
        Size = size;
        Flags = flags;
    }
}

public sealed class LowLevelImportMapEntryData
{
    public int Index { get; }
    public string ObjectName { get; }
    public string ClassName { get; }
    public string ClassPackage { get; }
    public string OuterName { get; }
    public string PackageName { get; }

    public LowLevelImportMapEntryData(
        int index,
        string objectName,
        string className,
        string classPackage,
        string outerName,
        string packageName)
    {
        Index = index;
        ObjectName = objectName;
        ClassName = className;
        ClassPackage = classPackage;
        OuterName = outerName;
        PackageName = packageName;
    }
}

public sealed class LowLevelNameMapEntryData
{
    public int Index { get; }
    public string Name { get; }
    public int UsageCount { get; }

    public LowLevelNameMapEntryData(int index, string name, int usageCount)
    {
        Index = index;
        Name = name;
        UsageCount = usageCount;
    }
}
