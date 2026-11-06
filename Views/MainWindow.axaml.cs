using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNotes.Models;
using DesktopNotes.ViewModels;

namespace DesktopNotes.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 主面板关闭 = 隐藏到托盘，应用继续运行（托盘不可用时允许真正关闭）
        Closing += (_, e) =>
        {
            if (!App.TrayAvailable) return;
            e.Cancel = true;
            Hide();
        };
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void NewNote_Click(object? sender, RoutedEventArgs e) => VM?.CreateNote(NoteType.Note);
    private void NewTodo_Click(object? sender, RoutedEventArgs e) => VM?.CreateNote(NoteType.Todo);
    private void NewTimeline_Click(object? sender, RoutedEventArgs e) => VM?.CreateNote(NoteType.Timeline);

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (VM != null && sender is Control { DataContext: NoteViewModel vm })
        {
            vm.Model.Collapsed = false;
            VM.OpenNote(vm);
        }
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (VM != null && sender is Control { DataContext: NoteViewModel vm })
            VM.DeleteNote(vm);
    }

    private void EmbedAll_Click(object? sender, RoutedEventArgs e) => VM?.EmbedAll(true);
    private void UnembedAll_Click(object? sender, RoutedEventArgs e) => VM?.EmbedAll(false);

    private void ImportSticky_Click(object? sender, RoutedEventArgs e) => VM?.ImportStickyNotes(true);

    private void ExportSticky_Click(object? sender, RoutedEventArgs e) => VM?.ExportStickyNotes();
}
