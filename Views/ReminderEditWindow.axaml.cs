using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DesktopNotes.Views;

public partial class ReminderEditWindow : Window
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    public DateTime SelectedTime { get; private set; }
    public bool ClearRequested { get; private set; }

    public ReminderEditWindow()
    {
        InitializeComponent();
    }

    public ReminderEditWindow(DateTime? reminder) : this()
    {
        var initialTime = reminder ?? DateTime.Now.AddMinutes(10);
        TimeInput.Text = initialTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
        ClearButton.IsVisible = reminder.HasValue;
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
            ValidationText.Text = "时间格式不正确，请按 yyyy-MM-dd HH:mm 输入。";
            ValidationText.IsVisible = true;
            TimeInput.Focus();
            return;
        }

        if (time <= DateTime.Now)
        {
            ValidationText.Text = "提醒时间必须晚于当前时间。";
            ValidationText.IsVisible = true;
            TimeInput.Focus();
            return;
        }

        SelectedTime = DateTime.SpecifyKind(time, DateTimeKind.Local);
        Close(true);
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        ClearRequested = true;
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
}