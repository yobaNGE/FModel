using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FModel.ViewModels.LowLevel;
using FModel.Settings;
using ViewTabItem = FModel.ViewModels.TabItem;

namespace FModel.Views.Resources.Controls;

public partial class LowLevelViewer : UserControl
{
    private readonly record struct NavigationState(string FunctionKey, int? ScriptOffset);

    private sealed class OperandGridRow
    {
        public LowLevelOperandNodeData Node { get; }
        public int Depth { get; }
        public LowLevelInstructionData Instruction { get; }

        public string DisplayName => $"{new string(' ', Depth * 2)}{Node.Name}";
        public string Kind => Node.Kind;
        public string Type => Node.Type;
        public string Value => Node.Value;
        public string Reason => Node.Reason ?? string.Empty;
        public string Bytes => Node.Bytes;
        public string RangeDisplay => Node.RangeDisplay;
        public string AssetRawRangeDisplay => GetAssetRawRangeDisplay();
        public int Length => Node.Length;
        public LowLevelOperandConfidence Confidence => Node.Confidence;

        public OperandGridRow(LowLevelInstructionData instruction, LowLevelOperandNodeData node, int depth)
        {
            Instruction = instruction;
            Node = node;
            Depth = depth;
        }

        private string GetAssetRawRangeDisplay()
        {
            if (!Node.HasRange)
                return "<unknown>";
            if (Instruction.OffsetConfidence == LowLevelOffsetConfidence.Fallback)
                return "<unknown>";

            var relative = Node.Start - Instruction.RawByteStart;
            if (relative < 0)
                return "<unknown>";

            try
            {
                var assetStart = checked(Instruction.RawOffset + relative);
                var assetEnd = checked(assetStart + Node.Length);
                return $"0x{assetStart:X8}..0x{assetEnd:X8}";
            }
            catch (OverflowException)
            {
                return "<unknown>";
            }
        }
    }

    private sealed class ColumnMenuPayload
    {
        public DataGrid Grid { get; }
        public DataGridColumn Column { get; }
        public string GridId { get; }
        public string ColumnId { get; }

        public ColumnMenuPayload(DataGrid grid, DataGridColumn column, string gridId, string columnId)
        {
            Grid = grid;
            Column = column;
            GridId = gridId;
            ColumnId = columnId;
        }
    }

    private ViewTabItem? _boundTab;
    private bool _isUpdatingSelection;
    private bool _suppressHistory;
    private bool _columnVisibilityInitialized;
    private readonly Stack<NavigationState> _backHistory = new();
    private readonly Stack<NavigationState> _forwardHistory = new();
    private readonly Dictionary<DataGrid, string> _gridIds = new();
    private readonly Dictionary<DataGridColumn, string> _columnIds = new();
    private readonly Dictionary<DataGridColumn, Visibility> _defaultColumnVisibility = new();
    private LowLevelOffsetDisplayMode _offsetMode = LowLevelOffsetDisplayMode.Raw;

    public LowLevelViewer()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OffsetModeComboBox.ItemsSource = Enum.GetValues(typeof(LowLevelOffsetDisplayMode));
        OffsetModeComboBox.SelectedItem = LowLevelOffsetDisplayMode.Raw;
        InitializeColumnVisibilityMenus();
        AttachTab();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => AttachTab();

    private void AttachTab()
    {
        if (_boundTab is not null)
            _boundTab.PropertyChanged -= OnTabPropertyChanged;

        _boundTab = DataContext as ViewTabItem;
        if (_boundTab is not null)
            _boundTab.PropertyChanged += OnTabPropertyChanged;

        RefreshAll();
    }

    private void InitializeColumnVisibilityMenus()
    {
        if (_columnVisibilityInitialized)
            return;

        RegisterGridForColumnVisibility(FunctionsGrid, "Functions");
        RegisterGridForColumnVisibility(InstructionsGrid, "Disasm");
        RegisterGridForColumnVisibility(OperandsGrid, "Operands");
        RegisterGridForColumnVisibility(CfgBlocksGrid, "CfgBlocks");
        RegisterGridForColumnVisibility(CfgEdgesGrid, "CfgEdges");
        RegisterGridForColumnVisibility(HexGrid, "Hex");
        RegisterGridForColumnVisibility(PropertyTagsGrid, "PropertyTags");
        RegisterGridForColumnVisibility(OutgoingXrefsGrid, "OutgoingXrefs");
        RegisterGridForColumnVisibility(IncomingXrefsGrid, "IncomingXrefs");
        RegisterGridForColumnVisibility(ExportMapGrid, "ExportMap");
        RegisterGridForColumnVisibility(ImportMapGrid, "ImportMap");
        RegisterGridForColumnVisibility(NameMapGrid, "NameMap");

        _columnVisibilityInitialized = true;
    }

    private void RegisterGridForColumnVisibility(DataGrid grid, string gridId)
    {
        _gridIds[grid] = gridId;
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            var columnId = BuildColumnId(gridId, column, i);
            _columnIds[column] = columnId;
            if (!_defaultColumnVisibility.ContainsKey(column))
                _defaultColumnVisibility[column] = column.Visibility;

            if (TryGetStoredColumnVisibility(gridId, columnId, out var visible))
                column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        EnsureAtLeastOneVisible(grid);
        grid.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnDataGridHeaderContextMenuOpening), true);
    }

    private void OnDataGridHeaderContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
            return;

        var header = FindParent<DataGridColumnHeader>(source);
        if (header?.Column is null || !_gridIds.TryGetValue(grid, out var gridId))
            return;

        header.ContextMenu = BuildColumnsContextMenu(grid, gridId);
    }

    private ContextMenu BuildColumnsContextMenu(DataGrid grid, string gridId)
    {
        var menu = new ContextMenu();
        foreach (var column in grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (!_columnIds.TryGetValue(column, out var columnId))
                continue;

            var menuItem = new MenuItem
            {
                Header = column.Header?.ToString() ?? columnId,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
                Tag = new ColumnMenuPayload(grid, column, gridId, columnId)
            };
            menuItem.Click += OnColumnMenuItemClicked;
            menu.Items.Add(menuItem);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        var resetItem = new MenuItem { Header = "Reset Columns", Tag = grid };
        resetItem.Click += OnResetColumnsClicked;
        menu.Items.Add(resetItem);
        return menu;
    }

    private void OnColumnMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ColumnMenuPayload payload } menuItem)
            return;

        var visible = menuItem.IsChecked;
        if (!visible && payload.Grid.Columns.Count(c => c.Visibility == Visibility.Visible) <= 1)
        {
            menuItem.IsChecked = true;
            return;
        }

        payload.Column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PersistColumnVisibility(payload.GridId, payload.ColumnId, visible);
    }

    private void OnResetColumnsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DataGrid grid } || !_gridIds.TryGetValue(grid, out var gridId))
            return;

        foreach (var column in grid.Columns)
        {
            if (!_columnIds.TryGetValue(column, out var columnId))
                continue;

            if (_defaultColumnVisibility.TryGetValue(column, out var visibility))
                column.Visibility = visibility;
            else
                column.Visibility = Visibility.Visible;

            RemoveColumnVisibility(gridId, columnId);
        }

        EnsureAtLeastOneVisible(grid);
    }

    private void EnsureAtLeastOneVisible(DataGrid grid)
    {
        if (grid.Columns.Count == 0 || grid.Columns.Any(c => c.Visibility == Visibility.Visible))
            return;

        grid.Columns[0].Visibility = Visibility.Visible;
    }

    private static string BuildColumnId(string gridId, DataGridColumn column, int index)
    {
        var header = column.Header?.ToString()?.Trim()?.Replace(" ", "_");
        var bindingPath = TryGetColumnBindingPath(column);
        return string.IsNullOrWhiteSpace(bindingPath)
            ? $"{gridId}:{index}:{header}"
            : $"{gridId}:{index}:{bindingPath}";
    }

    private static string? TryGetColumnBindingPath(DataGridColumn column)
    {
        if (column is DataGridBoundColumn boundColumn && boundColumn.Binding is Binding binding)
            return binding.Path?.Path;
        return null;
    }

    private static Dictionary<string, bool> GetOrCreateGridColumnSettings(string gridId)
    {
        UserSettings.Default.LowLevelColumnVisibility ??= new Dictionary<string, Dictionary<string, bool>>();
        if (!UserSettings.Default.LowLevelColumnVisibility.TryGetValue(gridId, out var settings))
        {
            settings = new Dictionary<string, bool>(StringComparer.Ordinal);
            UserSettings.Default.LowLevelColumnVisibility[gridId] = settings;
        }

        return settings;
    }

    private static bool TryGetStoredColumnVisibility(string gridId, string columnId, out bool visible)
    {
        visible = true;
        if (UserSettings.Default.LowLevelColumnVisibility is null)
            return false;
        if (!UserSettings.Default.LowLevelColumnVisibility.TryGetValue(gridId, out var gridSettings))
            return false;
        return gridSettings.TryGetValue(columnId, out visible);
    }

    private static void PersistColumnVisibility(string gridId, string columnId, bool visible)
    {
        var gridSettings = GetOrCreateGridColumnSettings(gridId);
        gridSettings[columnId] = visible;
    }

    private static void RemoveColumnVisibility(string gridId, string columnId)
    {
        if (UserSettings.Default.LowLevelColumnVisibility is null)
            return;
        if (!UserSettings.Default.LowLevelColumnVisibility.TryGetValue(gridId, out var gridSettings))
            return;

        gridSettings.Remove(columnId);
        if (gridSettings.Count == 0)
            UserSettings.Default.LowLevelColumnVisibility.Remove(gridId);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T target)
                return target;
            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewTabItem.LowLevelData) or nameof(ViewTabItem.LowLevelAutoSyncHex))
            Dispatcher.Invoke(RefreshAll);
    }

    private void RefreshAll()
    {
        var packageData = _boundTab?.LowLevelData;
        var classes = packageData?.Classes;

        _isUpdatingSelection = true;
        ClassSelector.ItemsSource = classes;
        ExportMapGrid.ItemsSource = packageData?.ExportMapEntries;
        ImportMapGrid.ItemsSource = packageData?.ImportMapEntries;
        NameMapGrid.ItemsSource = packageData?.NameMapEntries;
        _isUpdatingSelection = false;

        ResetNavigationHistory();

        if (classes is null || classes.Count == 0)
        {
            HeaderStatusText.Text = "No structured low-level data.";
            ClearFunctionDetails();
            return;
        }

        if (ClassSelector.SelectedIndex < 0 || ClassSelector.SelectedIndex >= classes.Count)
            ClassSelector.SelectedIndex = 0;

        HeaderStatusText.Text = string.IsNullOrWhiteSpace(packageData?.Warning)
            ? $"{packageData?.PackageType} | {classes.Count} class(es)"
            : $"{packageData?.PackageType} | {packageData?.Warning}";

        RefreshSelectedClass();
        PushCurrentNavigationState();
    }

    private void RefreshSelectedClass()
    {
        if (ClassSelector.SelectedItem is not LowLevelClassData selectedClass)
        {
            ClearFunctionDetails();
            return;
        }

        PropertyTagsGrid.ItemsSource = selectedClass.PropertyTags;
        RefreshFunctions();
    }

    private void RefreshFunctions()
    {
        if (ClassSelector.SelectedItem is not LowLevelClassData selectedClass)
            return;

        var previousKey = (FunctionsGrid.SelectedItem as LowLevelFunctionData)?.Key;
        var search = FilterTextBox.Text?.Trim();

        IEnumerable<LowLevelFunctionData> filtered = selectedClass.Functions;
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(f =>
                ContainsIgnoreCase(f.Name, search) ||
                ContainsIgnoreCase(f.Flags, search) ||
                ContainsIgnoreCase(f.ScriptOffsetSummary, search));
        }

        var list = filtered.ToList();
        _isUpdatingSelection = true;
        FunctionsGrid.ItemsSource = list;
        _isUpdatingSelection = false;

        if (list.Count == 0)
        {
            ClearFunctionDetails();
            return;
        }

        var selected = !string.IsNullOrEmpty(previousKey)
            ? list.FirstOrDefault(x => x.Key == previousKey) ?? list[0]
            : list[0];

        _isUpdatingSelection = true;
        FunctionsGrid.SelectedItem = selected;
        _isUpdatingSelection = false;

        RefreshFunctionDetails();
    }

    private void RefreshFunctionDetails()
    {
        if (FunctionsGrid.SelectedItem is not LowLevelFunctionData functionData)
        {
            ClearFunctionDetails();
            return;
        }

        RefreshInstructions(functionData);
        if (InstructionsGrid.SelectedItem is LowLevelInstructionData selectedInstruction)
            RefreshOperands(selectedInstruction);
        else
            OperandsGrid.ItemsSource = null;

        CfgBlocksGrid.ItemsSource = functionData.BasicBlocks;
        CfgEdgesGrid.ItemsSource = functionData.CfgEdges;
        HexGrid.ItemsSource = functionData.HexRows;
        OutgoingXrefsGrid.ItemsSource = functionData.OutgoingXrefs;
        IncomingXrefsGrid.ItemsSource = functionData.IncomingXrefs;

        var delta = functionData.RawToParserDelta.HasValue ? $"{functionData.RawToParserDelta.Value:+#;-#;0}" : "n/a";
        HeaderStatusText.Text = $"{functionData.OwnerClassName}::{functionData.Name} | script 0x{functionData.ScriptSize:X} | instr {functionData.InstructionCount} | delta {delta}";
        InspectorTextBox.Text = BuildFunctionInspectorText(functionData);
        SyncHexToInstruction();
    }

    private void RefreshInstructions(LowLevelFunctionData functionData)
    {
        var previousScriptOffset = (InstructionsGrid.SelectedItem as LowLevelInstructionData)?.ScriptOffset;
        var search = FilterTextBox.Text?.Trim();

        IEnumerable<LowLevelInstructionData> filtered = functionData.Instructions;
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(i =>
                ContainsIgnoreCase(i.Opcode, search) ||
                ContainsIgnoreCase(i.OpcodeCategory.ToString(), search) ||
                ContainsIgnoreCase(i.OpcodeByte.ToString("X2"), search) ||
                ContainsIgnoreCase(i.Decoded, search) ||
                ContainsIgnoreCase(i.Bytes, search) ||
                ContainsIgnoreCase(i.Label, search) ||
                ContainsIgnoreCase(i.JumpTargetLabel, search));
        }

        var instructionList = filtered.ToList();
        _isUpdatingSelection = true;
        InstructionsGrid.ItemsSource = instructionList;
        _isUpdatingSelection = false;

        if (instructionList.Count == 0)
        {
            InstructionsGrid.SelectedIndex = -1;
            OperandsGrid.ItemsSource = null;
            return;
        }

        var selectedInstruction = previousScriptOffset.HasValue
            ? instructionList.FirstOrDefault(x => x.ScriptOffset == previousScriptOffset.Value) ?? instructionList[0]
            : instructionList[0];

        _isUpdatingSelection = true;
        InstructionsGrid.SelectedItem = selectedInstruction;
        _isUpdatingSelection = false;
    }

    private void ClearFunctionDetails()
    {
        InstructionsGrid.ItemsSource = null;
        OperandsGrid.ItemsSource = null;
        CfgBlocksGrid.ItemsSource = null;
        CfgEdgesGrid.ItemsSource = null;
        HexGrid.ItemsSource = null;
        PropertyTagsGrid.ItemsSource = null;
        OutgoingXrefsGrid.ItemsSource = null;
        IncomingXrefsGrid.ItemsSource = null;
        InspectorTextBox.Text = string.Empty;
    }

    private void SyncHexToInstruction()
    {
        if (_boundTab is not { LowLevelAutoSyncHex: true } ||
            InstructionsGrid.SelectedItem is not LowLevelInstructionData instruction ||
            FunctionsGrid.SelectedItem is not LowLevelFunctionData functionData ||
            functionData.HexRows.Count == 0)
            return;

        var row = functionData.HexRows.FirstOrDefault(x => instruction.RawOffset >= x.Offset && instruction.RawOffset < x.Offset + 16);
        if (row is null)
            return;

        HexGrid.SelectedItem = row;
        HexGrid.ScrollIntoView(row);
    }

    private void SyncHexToRawOffset(long rawOffset)
    {
        if (_boundTab is not { LowLevelAutoSyncHex: true } ||
            FunctionsGrid.SelectedItem is not LowLevelFunctionData functionData ||
            functionData.HexRows.Count == 0)
            return;

        var row = functionData.HexRows.FirstOrDefault(x => rawOffset >= x.Offset && rawOffset < x.Offset + 16);
        if (row is null)
            return;

        HexGrid.SelectedItem = row;
        HexGrid.ScrollIntoView(row);
    }

    private void OnClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        RefreshSelectedClass();
        PushCurrentNavigationState();
    }

    private void OnFunctionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        RefreshFunctionDetails();
        PushCurrentNavigationState();
    }

    private void OnInstructionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (InstructionsGrid.SelectedItem is not LowLevelInstructionData instruction)
            return;

        SyncHexToInstruction();
        SelectCfgBlockByInstruction(instruction);
        RefreshOperands(instruction);
        InspectorTextBox.Text = BuildInstructionInspectorText(instruction);
        PushCurrentNavigationState();
    }

    private void OnOperandSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (OperandsGrid.SelectedItem is not OperandGridRow row)
            return;

        InspectorTextBox.Text = BuildOperandInspectorText(row);
        if (row.Node.HasRange && row.Instruction.OffsetConfidence != LowLevelOffsetConfidence.Fallback)
        {
            var relative = row.Node.Start - row.Instruction.RawByteStart;
            if (relative >= 0)
                SyncHexToRawOffset(row.Instruction.RawOffset + relative);
        }
    }

    private void OnCfgBlockSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (CfgBlocksGrid.SelectedItem is not LowLevelBasicBlockData block)
            return;

        SelectInstructionByIndex(block.FirstInstructionIndex);
        InspectorTextBox.Text = BuildCfgBlockInspectorText(block);
    }

    private void OnCfgEdgeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CfgEdgesGrid.SelectedItem is not LowLevelCfgEdgeData edge)
            return;

        InspectorTextBox.Text = $"CFG Edge{Environment.NewLine}From Block: {edge.FromBlockId}{Environment.NewLine}To Block: {edge.ToBlockId}{Environment.NewLine}Kind: {edge.Kind}{Environment.NewLine}Label: {edge.Label}";
    }

    private void OnOutgoingXrefSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutgoingXrefsGrid.SelectedItem is not LowLevelXrefData xref)
            return;

        InspectorTextBox.Text = BuildXrefInspectorText(xref);
    }

    private void OnIncomingXrefSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IncomingXrefsGrid.SelectedItem is not LowLevelXrefData xref)
            return;

        InspectorTextBox.Text = BuildXrefInspectorText(xref);
    }

    private void OnOutgoingXrefDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OutgoingXrefsGrid.SelectedItem is not LowLevelXrefData xref)
            return;

        if (!string.IsNullOrEmpty(xref.TargetFunctionKey))
            NavigateTo(xref.TargetFunctionKey, null);
    }

    private void OnIncomingXrefDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IncomingXrefsGrid.SelectedItem is not LowLevelXrefData xref)
            return;

        NavigateTo(xref.SourceFunctionKey, xref.SourceScriptOffset);
    }

    private void OnInstructionMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (InstructionsGrid.SelectedItem is not LowLevelInstructionData instruction || !instruction.JumpTargetScriptOffset.HasValue)
            return;

        NavigateToCurrentFunctionOffset(instruction.JumpTargetScriptOffset.Value);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        RefreshFunctions();
    }

    private void OnAutoSyncChanged(object sender, RoutedEventArgs e) => SyncHexToInstruction();

    private void OnOffsetModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OffsetModeComboBox.SelectedItem is LowLevelOffsetDisplayMode mode)
            _offsetMode = mode;
    }

    private void OnGoToOffsetClicked(object sender, RoutedEventArgs e)
    {
        if (!TryParseOffset(GoToOffsetTextBox.Text, out var offset))
            return;

        if (FunctionsGrid.SelectedItem is not LowLevelFunctionData functionData)
            return;

        var match = _offsetMode switch
        {
            LowLevelOffsetDisplayMode.Raw => functionData.Instructions.FirstOrDefault(x => offset >= x.RawOffset && offset < x.RawOffset + Math.Max(1, x.Length)),
            LowLevelOffsetDisplayMode.Parser => functionData.Instructions.FirstOrDefault(x => offset >= x.ParserOffset && offset < x.ParserOffset + Math.Max(1, x.Length)),
            LowLevelOffsetDisplayMode.Script => functionData.Instructions.FirstOrDefault(x => offset >= x.ScriptOffset && offset < x.ScriptOffset + Math.Max(1, x.Length)),
            _ => null
        };
        if (match is null)
            return;

        NavigateToCurrentFunctionOffset(match.ScriptOffset);
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_backHistory.Count <= 1)
            return;

        var current = _backHistory.Pop();
        _forwardHistory.Push(current);
        var previous = _backHistory.Peek();
        ApplyNavigationState(previous, false);
    }

    private void OnForwardClicked(object sender, RoutedEventArgs e)
    {
        if (_forwardHistory.Count == 0)
            return;

        var next = _forwardHistory.Pop();
        ApplyNavigationState(next, true);
    }

    private void OnExportMapDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExportMapGrid.SelectedItem is not LowLevelExportMapEntryData selectedExport)
            return;

        var classData = _boundTab?.LowLevelData?.Classes.FirstOrDefault(c => c.ExportIndex == selectedExport.Index);
        if (classData is not null)
        {
            _isUpdatingSelection = true;
            ClassSelector.SelectedItem = classData;
            _isUpdatingSelection = false;
            RefreshSelectedClass();
            return;
        }

        var function = _boundTab?.LowLevelData?.Classes.SelectMany(x => x.Functions).FirstOrDefault(x => x.ExportIndex == selectedExport.Index);
        if (function is not null)
            NavigateTo(function.Key, null);
    }

    private void OnNameMapDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NameMapGrid.SelectedItem is not LowLevelNameMapEntryData selectedName)
            return;

        _isUpdatingSelection = true;
        FilterTextBox.Text = selectedName.Name;
        _isUpdatingSelection = false;
        RefreshFunctions();
    }

    private void SelectCfgBlockByInstruction(LowLevelInstructionData instruction)
    {
        if (FunctionsGrid.SelectedItem is not LowLevelFunctionData function)
            return;

        var block = function.BasicBlocks.FirstOrDefault(x => x.Id == instruction.BasicBlockId);
        if (block is null)
            return;

        _isUpdatingSelection = true;
        CfgBlocksGrid.SelectedItem = block;
        _isUpdatingSelection = false;
    }

    private void SelectInstructionByIndex(int instructionIndex)
    {
        if (InstructionsGrid.ItemsSource is not IEnumerable<LowLevelInstructionData> rows)
            return;

        var target = rows.FirstOrDefault(x => x.Index == instructionIndex);
        if (target is null)
            return;

        _isUpdatingSelection = true;
        InstructionsGrid.SelectedItem = target;
        _isUpdatingSelection = false;
        InstructionsGrid.ScrollIntoView(target);
        MainTabs.SelectedIndex = 0;
    }

    private void NavigateToCurrentFunctionOffset(int scriptOffset)
    {
        if (FunctionsGrid.SelectedItem is not LowLevelFunctionData functionData)
            return;

        NavigateTo(functionData.Key, scriptOffset);
    }

    private void SelectInstructionByScriptOffset(int scriptOffset)
    {
        if (InstructionsGrid.ItemsSource is not IEnumerable<LowLevelInstructionData> rows)
            return;

        var target = rows.FirstOrDefault(x => x.ScriptOffset == scriptOffset);
        if (target is null)
            return;

        _isUpdatingSelection = true;
        InstructionsGrid.SelectedItem = target;
        _isUpdatingSelection = false;
        InstructionsGrid.ScrollIntoView(target);
        MainTabs.SelectedIndex = 0;
        SyncHexToInstruction();
    }

    private void NavigateTo(string functionKey, int? scriptOffset)
    {
        ApplyNavigationState(new NavigationState(functionKey, scriptOffset), true);
    }

    private void ApplyNavigationState(NavigationState state, bool addToHistory)
    {
        var packageData = _boundTab?.LowLevelData;
        if (packageData is null)
            return;

        var targetClass = packageData.Classes.FirstOrDefault(c => c.Functions.Any(f => f.Key == state.FunctionKey));
        if (targetClass is null)
            return;

        var targetFunction = targetClass.Functions.FirstOrDefault(f => f.Key == state.FunctionKey);
        if (targetFunction is null)
            return;

        _suppressHistory = true;
        _isUpdatingSelection = true;
        if (ClassSelector.SelectedItem != targetClass)
            ClassSelector.SelectedItem = targetClass;
        _isUpdatingSelection = false;

        RefreshSelectedClass();

        _isUpdatingSelection = true;
        FunctionsGrid.SelectedItem = (FunctionsGrid.ItemsSource as IEnumerable<LowLevelFunctionData>)?.FirstOrDefault(x => x.Key == targetFunction.Key);
        _isUpdatingSelection = false;
        RefreshFunctionDetails();

        if (state.ScriptOffset.HasValue)
            SelectInstructionByScriptOffset(state.ScriptOffset.Value);

        _suppressHistory = false;
        if (addToHistory)
            PushCurrentNavigationState();
    }

    private void PushCurrentNavigationState()
    {
        if (_suppressHistory)
            return;

        if (FunctionsGrid.SelectedItem is not LowLevelFunctionData function)
            return;

        var state = new NavigationState(function.Key, (InstructionsGrid.SelectedItem as LowLevelInstructionData)?.ScriptOffset);
        if (_backHistory.Count > 0 && _backHistory.Peek().Equals(state))
            return;

        _backHistory.Push(state);
        _forwardHistory.Clear();
    }

    private void ResetNavigationHistory()
    {
        _backHistory.Clear();
        _forwardHistory.Clear();
    }

    private static bool TryParseOffset(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool ContainsIgnoreCase(string? value, string needle) =>
        !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string BuildFunctionInspectorText(LowLevelFunctionData functionData)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Function: {functionData.OwnerClassName}::{functionData.Name}");
        sb.AppendLine($"Export: #{functionData.ExportIndex + 1}");
        sb.AppendLine($"Flags: {functionData.Flags}");
        sb.AppendLine($"Script size: 0x{functionData.ScriptSize:X}");
        sb.AppendLine($"Instructions: {functionData.InstructionCount}");
        sb.AppendLine($"Basic blocks: {functionData.BasicBlocks.Count}");
        sb.AppendLine($"CFG edges: {functionData.CfgEdges.Count}");
        sb.AppendLine($"Outgoing xrefs: {functionData.OutgoingXrefs.Count}");
        sb.AppendLine($"Incoming xrefs: {functionData.IncomingXrefs.Count}");
        if (!string.IsNullOrWhiteSpace(functionData.Warning))
            sb.AppendLine($"Warning: {functionData.Warning}");
        return sb.ToString();
    }

    private static string BuildInstructionInspectorText(LowLevelInstructionData instruction)
    {
        var sb = new StringBuilder();
        var (mappedBytes, unknownBytes, totalBytes) = ComputeOperandCoverage(instruction);
        sb.AppendLine($"Instruction #{instruction.Index}");
        sb.AppendLine($"Raw: 0x{instruction.RawOffset:X8}");
        sb.AppendLine($"Parser: 0x{instruction.ParserOffset:X8}");
        sb.AppendLine($"Script: 0x{instruction.ScriptOffset:X4}");
        sb.AppendLine($"Length: {instruction.Length}");
        sb.AppendLine($"Raw bytes: {instruction.RawByteRangeDisplay}");
        sb.AppendLine($"Raw range source: {instruction.RawRangeSource}");
        sb.AppendLine($"Raw range valid: {instruction.RawRangeValid}");
        sb.AppendLine($"Block: {instruction.BasicBlockId}");
        sb.AppendLine($"Offset confidence: {instruction.OffsetConfidence}");
        sb.AppendLine($"Opcode: {instruction.Opcode}");
        sb.AppendLine($"Opcode byte: 0x{instruction.OpcodeByte:X2}");
        sb.AppendLine($"Opcode category: {instruction.OpcodeCategory}");
        if (!string.IsNullOrWhiteSpace(instruction.Label))
            sb.AppendLine($"Label: {instruction.Label}");
        if (!string.IsNullOrWhiteSpace(instruction.JumpTargetLabel))
            sb.AppendLine($"Jump: {instruction.JumpTargetLabel}");
        sb.AppendLine($"Bytes: {instruction.Bytes}");
        sb.AppendLine($"Decoded: {instruction.Decoded}");
        sb.AppendLine($"Operands: {instruction.Operands.Count}");
        sb.AppendLine($"Operand coverage: {mappedBytes}/{totalBytes} mapped, {unknownBytes} unknown");
        return sb.ToString();
    }

    private void RefreshOperands(LowLevelInstructionData instruction)
    {
        var rows = new List<OperandGridRow>();
        foreach (var operand in instruction.Operands)
            AppendOperandRows(rows, instruction, operand, 0);

        _isUpdatingSelection = true;
        OperandsGrid.ItemsSource = rows;
        OperandsGrid.SelectedIndex = rows.Count > 0 ? 0 : -1;
        _isUpdatingSelection = false;
    }

    private static void AppendOperandRows(List<OperandGridRow> rows, LowLevelInstructionData instruction, LowLevelOperandNodeData node, int depth)
    {
        rows.Add(new OperandGridRow(instruction, node, depth));
        foreach (var child in node.Children)
            AppendOperandRows(rows, instruction, child, depth + 1);
    }

    private static string BuildOperandInspectorText(OperandGridRow row)
    {
        var node = row.Node;
        var sb = new StringBuilder();
        sb.AppendLine($"Operand: {node.Name}");
        sb.AppendLine($"Kind: {node.Kind}");
        sb.AppendLine($"Type: {node.Type}");
        sb.AppendLine($"Range: {node.RangeDisplay}");
        sb.AppendLine($"Asset Raw Range: {row.AssetRawRangeDisplay}");
        sb.AppendLine($"Length: {node.Length}");
        sb.AppendLine($"Confidence: {node.Confidence}");
        if (!string.IsNullOrWhiteSpace(node.Reason))
            sb.AppendLine($"Reason: {node.Reason}");
        sb.AppendLine($"Bytes: {node.Bytes}");
        sb.AppendLine($"Value: {node.Value}");
        sb.AppendLine($"Children: {node.Children.Count}");
        return sb.ToString();
    }

    private static (int MappedBytes, int UnknownBytes, int TotalBytes) ComputeOperandCoverage(LowLevelInstructionData instruction)
    {
        var totalBytes = Math.Max(0, instruction.RawByteEnd - instruction.RawByteStart);
        if (totalBytes == 0 || instruction.Operands.Count == 0)
            return (0, 0, totalBytes);

        var leaves = new List<LowLevelOperandNodeData>();
        foreach (var node in instruction.Operands)
            CollectLeafNodes(node, leaves);

        var mappedRanges = leaves
            .Where(x => x.Kind != "Unknown")
            .Select(x => (x.Start, x.End));
        var unknownRanges = leaves
            .Where(x => x.Kind == "Unknown")
            .Select(x => (x.Start, x.End));

        var mappedBytes = ComputeCoveredLength(mappedRanges, instruction.RawByteStart, instruction.RawByteEnd);
        var unknownBytes = ComputeCoveredLength(unknownRanges, instruction.RawByteStart, instruction.RawByteEnd);
        return (mappedBytes, unknownBytes, totalBytes);
    }

    private static void CollectLeafNodes(LowLevelOperandNodeData node, List<LowLevelOperandNodeData> output)
    {
        if (node.Children.Count == 0)
        {
            output.Add(node);
            return;
        }

        foreach (var child in node.Children)
            CollectLeafNodes(child, output);
    }

    private static int ComputeCoveredLength(IEnumerable<(int Start, int End)> ranges, int clampStart, int clampEnd)
    {
        if (clampEnd <= clampStart)
            return 0;

        var sortedRanges = ranges
            .Where(x => x.End > x.Start)
            .Select(x => (Start: Math.Max(clampStart, x.Start), End: Math.Min(clampEnd, x.End)))
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start)
            .ToList();
        if (sortedRanges.Count == 0)
            return 0;

        var covered = 0;
        var currentStart = sortedRanges[0].Start;
        var currentEnd = sortedRanges[0].End;
        for (var i = 1; i < sortedRanges.Count; i++)
        {
            var next = sortedRanges[i];
            if (next.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, next.End);
                continue;
            }

            covered += currentEnd - currentStart;
            currentStart = next.Start;
            currentEnd = next.End;
        }

        covered += currentEnd - currentStart;
        return covered;
    }

    private static string BuildCfgBlockInspectorText(LowLevelBasicBlockData block)
    {
        return $"CFG Block {block.Id}{Environment.NewLine}" +
               $"Script Range: {block.ScriptRangeDisplay}{Environment.NewLine}" +
               $"Instructions: {block.FirstInstructionIndex}..{block.LastInstructionIndex}{Environment.NewLine}" +
               $"Summary: {block.Summary}";
    }

    private static string BuildXrefInspectorText(LowLevelXrefData xref)
    {
        return $"Xref ({xref.DirectionLabel}){Environment.NewLine}" +
               $"Kind: {xref.Kind}{Environment.NewLine}" +
               $"Source: {xref.SourceFunctionName} @ 0x{xref.SourceScriptOffset:X4}{Environment.NewLine}" +
               $"Target: {xref.TargetDisplay}{Environment.NewLine}" +
               $"Target kind: {xref.TargetKind}{Environment.NewLine}" +
               $"Resolved: {xref.IsResolved}";
    }
}
