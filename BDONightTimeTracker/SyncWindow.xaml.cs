using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BDONightTimeTracker;

public partial class SyncWindow : Window
{
    // Valori letti al momento della conferma, esposti per il chiamante (MainWindow).
    public int SelectedHour   { get; private set; }
    public int SelectedMinute { get; private set; }

    public SyncWindow()
    {
        InitializeComponent();

        // Pre-popola con l'ora reale corrente come punto di partenza visivo.
        // L'utente la sovrascriverà con quella che legge nel gioco.
        HourBox.Text   = DateTime.Now.Hour.ToString("D2");
        MinuteBox.Text = DateTime.Now.Minute.ToString("D2");
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseTime(out int h, out int m))
        {
            // Mostra un errore inline senza aprire una MessageBox (meno intrusivo).
            ErrorLabel.Visibility = Visibility.Visible;
            HourBox.BorderBrush   = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x55));
            MinuteBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x55));
            // Note: ErrorLabel text is already set in XAML ("Invalid time (e.g. 14:30)")
            return;
        }

        SelectedHour   = h;
        SelectedMinute = m;

        // Impostare DialogResult a true chiude la finestra e fa sì che
        // ShowDialog() restituisca true al chiamante (MainWindow).
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Filtra l'input accettando solo cifre.
    /// PreviewTextInput scatta prima che il carattere venga inserito nella TextBox;
    /// impostare e.Handled = true blocca l'inserimento.
    /// </summary>
    private void TimeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>
    /// Al focus seleziona tutto il testo: così l'utente può sovrascrivere
    /// direttamente il valore pre-compilato senza dovere cancellare manualmente.
    /// </summary>
    private void TimeBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
            tb.SelectAll();
    }

    private void MainBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private bool TryParseTime(out int hour, out int minute)
    {
        hour   = 0;
        minute = 0;

        if (!int.TryParse(HourBox.Text,   out hour)   || hour   < 0 || hour   > 23) return false;
        if (!int.TryParse(MinuteBox.Text, out minute)  || minute < 0 || minute > 59) return false;

        return true;
    }
}
