using System.Windows;

namespace MinerU25Tool
{
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }
    }
}
