using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace DesktopNotes.Views;

public partial class TimelineEntryEditWindow : Window
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public DateTime SelectedTime { get; private set; }
    public string EntryText { get; private set; } = "";

    public TimelineEntryEditWindow()
    {
        InitializeComponent();
    }

    public TimelineEntryEditWindow(DateTime time, string text, bool isNew = false) : this()
    {
        Title = isNew ? "新增时间轴条目" : "编辑时间轴条目";
        TimeInput.Text = time.ToString(TimeFormat, CultureInfo.InvariantCulture);
        ContentInput.Text = text;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TimeInput.Focus();
        TimeInput.SelectAll();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!DateTime.TryParseExact(TimeInput.Text, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            ValidationText.Text = "时间格式不正确，请按 yyyy-MM-dd HH:mm:ss 输入。";
            ValidationText.IsVisible = true;
            TimeInput.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(ContentInput.Text))
        {
            ValidationText.Text = "内容不能为空。";
            ValidationText.IsVisible = true;
            ContentInput.Focus();
            return;
        }

        SelectedTime = time;
        EntryText = ContentInput.Text.Trim();
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void TimeInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ContentInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
            e.Handled = true;
        }
    }
}