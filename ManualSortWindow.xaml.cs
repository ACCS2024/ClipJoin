using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClipJoin
{
    public class FileItem
    {
        public string Index { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
    }

    public partial class ManualSortWindow : Window
    {
        private readonly FolderAnalysisWrapper _analysis;
        private readonly Dictionary<string, List<string>> _originalOrders = new();
        private readonly Dictionary<string, ObservableCollection<FileItem>> _groupItems = new();

        private Point _dragStartPoint;

        /// <summary>
        /// After confirmation, contains the updated analysis with reordered files.
        /// </summary>
        public FolderAnalysisWrapper? Result { get; private set; }

        public ManualSortWindow(FolderAnalysisWrapper analysis)
        {
            _analysis = analysis;
            InitializeComponent();
            InitializeGroups();

            KeyDown += ManualSortWindow_KeyDown;
        }

        private void InitializeGroups()
        {
            if (_analysis.Groups.Count > 1)
            {
                GroupSelectorPanel.Visibility = Visibility.Visible;
                foreach (var g in _analysis.Groups)
                {
                    GroupComboBox.Items.Add($"{g.Name} ({g.VideoFiles.Count} 个文件)");
                }
            }

            foreach (var g in _analysis.Groups)
            {
                _originalOrders[g.Name] = [.. g.VideoFiles];
                _groupItems[g.Name] = BuildFileItems(g.VideoFiles);
            }

            if (_analysis.Groups.Count > 0)
            {
                if (GroupComboBox.Items.Count > 0)
                    GroupComboBox.SelectedIndex = 0;
                else
                    FileListBox.ItemsSource = _groupItems[_analysis.Groups[0].Name];
            }
        }

        private static ObservableCollection<FileItem> BuildFileItems(List<string> paths)
        {
            var items = new ObservableCollection<FileItem>();
            for (int i = 0; i < paths.Count; i++)
            {
                items.Add(new FileItem
                {
                    Index = $"{i + 1}.",
                    FileName = Path.GetFileName(paths[i]),
                    FullPath = paths[i]
                });
            }
            return items;
        }

        private ObservableCollection<FileItem>? CurrentItems
        {
            get
            {
                if (_analysis.Groups.Count == 1)
                    return _groupItems[_analysis.Groups[0].Name];

                if (GroupComboBox.SelectedIndex >= 0 && GroupComboBox.SelectedIndex < _analysis.Groups.Count)
                    return _groupItems[_analysis.Groups[GroupComboBox.SelectedIndex].Name];

                return null;
            }
        }

        private void GroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CurrentItems != null)
                FileListBox.ItemsSource = CurrentItems;
        }

        private void RefreshIndices(ObservableCollection<FileItem> items)
        {
            for (int i = 0; i < items.Count; i++)
                items[i].Index = $"{i + 1}.";

            // Force refresh
            FileListBox.ItemsSource = null;
            FileListBox.ItemsSource = items;
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(-1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(1);
        }

        private void MoveSelected(int direction)
        {
            var items = CurrentItems;
            if (items == null) return;

            var selectedIndex = FileListBox.SelectedIndex;
            if (selectedIndex < 0) return;

            var newIndex = selectedIndex + direction;
            if (newIndex < 0 || newIndex >= items.Count) return;

            var item = items[selectedIndex];
            items.RemoveAt(selectedIndex);
            items.Insert(newIndex, item);
            RefreshIndices(items);
            FileListBox.SelectedIndex = newIndex;
            FileListBox.ScrollIntoView(items[newIndex]);
        }

        private void MoveToTop_Click(object sender, RoutedEventArgs e)
        {
            var items = CurrentItems;
            if (items == null) return;

            var selectedIndex = FileListBox.SelectedIndex;
            if (selectedIndex <= 0) return;

            var item = items[selectedIndex];
            items.RemoveAt(selectedIndex);
            items.Insert(0, item);
            RefreshIndices(items);
            FileListBox.SelectedIndex = 0;
            FileListBox.ScrollIntoView(items[0]);
        }

        private void MoveToBottom_Click(object sender, RoutedEventArgs e)
        {
            var items = CurrentItems;
            if (items == null) return;

            var selectedIndex = FileListBox.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= items.Count - 1) return;

            var item = items[selectedIndex];
            items.RemoveAt(selectedIndex);
            items.Add(item);
            RefreshIndices(items);
            FileListBox.SelectedIndex = items.Count - 1;
            FileListBox.ScrollIntoView(items[^1]);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _analysis.Groups)
            {
                if (_originalOrders.TryGetValue(g.Name, out var original))
                {
                    _groupItems[g.Name] = BuildFileItems(original);
                }
            }

            if (CurrentItems != null)
                FileListBox.ItemsSource = CurrentItems;
        }

        private void ManualSortWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Up) { MoveSelected(-1); e.Handled = true; }
                else if (e.Key == Key.Down) { MoveSelected(1); e.Handled = true; }
            }
        }

        // --- Drag-and-drop reordering ---

        private void FileListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void FileListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            if (FileListBox.SelectedItem is FileItem item)
            {
                DragDrop.DoDragDrop(FileListBox, item, DragDropEffects.Move);
            }
        }

        private void FileListBox_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(FileItem))
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void FileListBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(FileItem))) return;
            var items = CurrentItems;
            if (items == null) return;

            var droppedItem = (FileItem)e.Data.GetData(typeof(FileItem))!;

            // Find drop target
            var targetElement = e.OriginalSource as FrameworkElement;
            FileItem? targetItem = null;
            while (targetElement != null)
            {
                if (targetElement.DataContext is FileItem fi && fi != droppedItem)
                {
                    targetItem = fi;
                    break;
                }
                targetElement = targetElement.Parent as FrameworkElement;
            }

            if (targetItem == null) return;

            var oldIndex = items.IndexOf(droppedItem);
            var newIndex = items.IndexOf(targetItem);
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;

            items.RemoveAt(oldIndex);
            items.Insert(newIndex, droppedItem);
            RefreshIndices(items);
            FileListBox.SelectedIndex = newIndex;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            // Apply sorted orders back to analysis
            Result = _analysis;
            foreach (var g in Result.Groups)
            {
                if (_groupItems.TryGetValue(g.Name, out var items))
                {
                    g.VideoFiles = items.Select(fi => fi.FullPath).ToList();
                }
            }

            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    /// <summary>
    /// Mutable wrapper for FolderAnalysis to allow reordering video files.
    /// </summary>
    public class FolderAnalysisWrapper
    {
        public Services.FolderMode Mode { get; set; }
        public List<MergeGroupWrapper> Groups { get; set; } = [];

        public static FolderAnalysisWrapper FromAnalysis(Services.FolderAnalysis analysis)
        {
            return new FolderAnalysisWrapper
            {
                Mode = analysis.Mode,
                Groups = analysis.Groups.Select(g => new MergeGroupWrapper
                {
                    Name = g.Name,
                    SourceDir = g.SourceDir,
                    VideoFiles = [.. g.VideoFiles],
                    ImageFiles = [.. g.ImageFiles]
                }).ToList()
            };
        }

        public Services.FolderAnalysis ToAnalysis()
        {
            return new Services.FolderAnalysis
            {
                Mode = Mode,
                Groups = Groups.Select(g => new Services.MergeGroup
                {
                    Name = g.Name,
                    SourceDir = g.SourceDir,
                    VideoFiles = [.. g.VideoFiles],
                    ImageFiles = [.. g.ImageFiles]
                }).ToList()
            };
        }
    }

    public class MergeGroupWrapper
    {
        public string Name { get; set; } = "";
        public string SourceDir { get; set; } = "";
        public List<string> VideoFiles { get; set; } = [];
        public List<string> ImageFiles { get; set; } = [];
    }
}
