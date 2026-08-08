using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenUtau.App.ViewModels;

namespace OpenUtau.App.Views {
    public partial class ApplyPitchDialog : Window {
        public bool Confirmed { get; private set; }

        public ApplyPitchDialog() {
            InitializeComponent();
        }

        void OnOkClicked(object? sender, RoutedEventArgs e) {
            var vm = DataContext as ApplyPitchViewModel;
            if (vm?.CanRun == true) {
                Confirmed = true;
                Close();
            }
        }

        void OnCancelClicked(object? sender, RoutedEventArgs e) {
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                Close();
            } else if (e.Key == Key.Return) {
                var vm = DataContext as ApplyPitchViewModel;
                if (vm?.CanRun == true) {
                    e.Handled = true;
                    Confirmed = true;
                    Close();
                }
            } else {
                base.OnKeyDown(e);
            }
        }
    }
}
