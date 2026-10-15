using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Controls;
using SententiamConverte.Core.Model;
using SententiamConverte.ViewModels;

namespace SententiamConverte.Views;

/// <summary>
/// Editor mit den in Übersetzungsprogrammen üblichen Tastenkürzeln. Die Kürzel werden im Tunnel abgefangen,
/// damit das Textfeld sie nicht vorher verschluckt.
/// </summary>
public partial class EditorView : UserControl
{
    private EditorViewModel? _vm;
    private bool? _narrow;

    public EditorView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width < 900);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.FocusRequested -= FocusSegment;
        _vm = DataContext as EditorViewModel;
        if (_vm != null) _vm.FocusRequested += FocusSegment;
    }

    /// <summary>Schmale Bildschirme (Telefon): Seitenleiste unter die Segmentliste.</summary>
    private void ApplyLayout(bool narrow)
    {
        if (_narrow == narrow) return;
        _narrow = narrow;
        Split.ColumnDefinitions.Clear();
        Split.RowDefinitions.Clear();
        if (narrow)
        {
            Split.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Split.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Split.RowDefinitions.Add(new RowDefinition(new GridLength(320)));
            Grid.SetColumn(SegmentList, 0); Grid.SetRow(SegmentList, 0);
            Grid.SetColumn(Splitter, 0); Grid.SetRow(Splitter, 1);
            Grid.SetColumn(SidePanel, 0); Grid.SetRow(SidePanel, 2);
            Splitter.ResizeDirection = GridResizeDirection.Rows;
            Splitter.Width = double.NaN;
            Splitter.Height = 5;
        }
        else
        {
            Split.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Split.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Split.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(400)));
            Grid.SetRow(SegmentList, 0); Grid.SetColumn(SegmentList, 0);
            Grid.SetRow(Splitter, 0); Grid.SetColumn(Splitter, 1);
            Grid.SetRow(SidePanel, 0); Grid.SetColumn(SidePanel, 2);
            Splitter.ResizeDirection = GridResizeDirection.Columns;
            Splitter.Height = double.NaN;
            Splitter.Width = 5;
        }
    }

    private void OnTargetFocused(object? sender, FocusChangedEventArgs e)
    {
        if (sender is Control { DataContext: SegmentViewModel seg } && _vm != null && _vm.Selected != seg)
            _vm.Selected = seg;
    }

    // ---------- Teilen ----------

    private void OnSplitClick(object? sender, RoutedEventArgs e) => SplitAtSourceCursor();

    private void SplitAtSourceCursor()
    {
        if (_vm?.Selected == null) return;
        var (src, _) = RowControls(_vm.Selected);
        if (src == null) return;
        var display = Math.Min(src.SelectionStart, src.SelectionEnd);
        if (display <= 0)
        {
            _vm.SplitAt(-1);   // meldet „hier nicht möglich“
            return;
        }
        _vm.SplitAt(TagHighlight.DisplayToSourceIndex(_vm.Selected.Source, display));
    }

    // ---------- AutoSuggest ----------

    private TextBox? _suggestTarget;
    private bool _inserting;

    private void OnTargetTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_inserting || _vm == null || sender is not TextBox { IsFocused: true } box) return;
        var text = box.Text ?? "";
        var caret = Math.Min(box.CaretIndex, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] != TagText.Close) start--;
        var prefix = text[start..caret];
        var list = _vm.SuggestionsFor(prefix);
        if (list.Count == 0)
        {
            SuggestPopup.IsOpen = false;
            return;
        }
        _suggestTarget = box;
        SuggestList.ItemsSource = list;
        SuggestList.SelectedIndex = 0;
        SuggestPopup.PlacementTarget = box;
        SuggestPopup.IsOpen = true;
    }

    private void OnSuggestionTapped(object? sender, TappedEventArgs e) => AcceptSuggestion();

    private void AcceptSuggestion()
    {
        if (_suggestTarget is not { } box || SuggestList.SelectedItem is not string suggestion) return;
        var text = box.Text ?? "";
        var caret = Math.Min(box.CaretIndex, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] != TagText.Close) start--;
        _inserting = true;
        box.Text = text[..start] + suggestion + text[caret..];
        box.CaretIndex = start + suggestion.Length;
        _inserting = false;
        SuggestPopup.IsOpen = false;
        box.Focus();
    }

    private void OnQaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (QaList.SelectedItem is QaRow row) _vm?.GoToSegmentCommand.Execute(row);
    }

    private (SelectableTextBlock? Source, TextBox? Target) RowControls(SegmentViewModel seg)
    {
        if (SegmentList.ContainerFromItem(seg) is not Control container) return (null, null);
        var descendants = container.GetVisualDescendants().ToList();
        return (descendants.OfType<SelectableTextBlock>().FirstOrDefault(), descendants.OfType<TextBox>().FirstOrDefault());
    }

    private void FocusSegment(SegmentViewModel seg)
    {
        SegmentList.ScrollIntoView(seg);
        Dispatcher.UIThread.Post(() =>
        {
            var (_, box) = RowControls(seg);
            if (box == null) return;
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var handled = true;

        if (SuggestPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    SuggestList.SelectedIndex = Math.Min(SuggestList.SelectedIndex + 1, SuggestList.ItemCount - 1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    SuggestList.SelectedIndex = Math.Max(SuggestList.SelectedIndex - 1, 0);
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Enter when !ctrl:
                    AcceptSuggestion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    SuggestPopup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Enter when ctrl && alt:
                _vm.SetReviewedCommand.Execute(null);
                break;
            case Key.Enter when ctrl:
                _vm.ConfirmAndNextCommand.Execute(null);
                break;
            case Key.Down when ctrl:
                _vm.MoveNextCommand.Execute(false);
                break;
            case Key.Up when ctrl:
                _vm.MovePreviousCommand.Execute(null);
                break;
            case Key.Insert when ctrl:
                _vm.CopySourceCommand.Execute(null);
                break;
            case Key.Delete when ctrl && !shift:
                _vm.ClearTargetCommand.Execute(null);
                break;
            case Key.S when ctrl && !shift:
                _vm.SaveCommand.Execute(null);
                break;
            case Key.T when ctrl && shift:
                _vm.AiTranslateCommand.Execute(null);
                break;
            case Key.E when ctrl && shift:
                _vm.EvaluateSegmentCommand.Execute(null);
                break;
            case Key.U when ctrl && shift:
                _ = _vm.CheckUsageAsync(SelectedTargetText() ?? TagText.StripTags(_vm.Selected?.Target ?? ""));
                break;
            case Key.F3:
                var text = SelectedSourceText() ?? SelectedTargetText();
                if (text != null)
                {
                    _vm.ConcordanceInTarget = SelectedSourceText() == null;
                    _vm.ConcordanceFor(text);
                }
                break;
            case Key.F2 when ctrl:
                var src = SelectedSourceText();
                if (src != null) _vm.AddTerm(src, SelectedTargetText() ?? "");
                break;
            case Key.T when alt && !ctrl:
                SplitAtSourceCursor();
                break;
            case Key.Z when alt && !ctrl:
                _vm.MergeWithNextCommand.Execute(null);
                break;
            case >= Key.D1 and <= Key.D9 when ctrl && !shift:
                _vm.ApplyMatchCommand.Execute(e.Key - Key.D0);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    private string? SelectedSourceText()
    {
        if (_vm?.Selected == null) return null;
        var (src, _) = RowControls(_vm.Selected);
        var t = src?.SelectedText is { } shown ? TagHighlight.DisplayToPlain(shown) : null;
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    private string? SelectedTargetText()
    {
        if (_vm?.Selected == null) return null;
        var (_, box) = RowControls(_vm.Selected);
        var t = box?.SelectedText;
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }
}
