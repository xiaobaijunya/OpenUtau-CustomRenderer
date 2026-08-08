using System;
using OpenUtau.Core.Analysis;
using OpenUtau.Core.Editing;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace OpenUtau.App.ViewModels {
    public class ApplyPitchViewModel : ViewModelBase {
        public bool RmvpeAvailable { get; }
        public bool CrepeAvailable { get; }
        public bool WorldDioAvailable { get; }
        public bool WorldHarvestAvailable { get; }
        public bool WorldPyinAvailable { get; }

        [Reactive]
        public PitchExtractionMethod SelectedMethod { get; set; } = PitchExtractionMethod.Rmvpe;

        // Convenience bool bindings for RadioButtons
        public bool UseRmvpe {
            get => SelectedMethod == PitchExtractionMethod.Rmvpe;
            set {
                if (value) {
                    SelectedMethod = PitchExtractionMethod.Rmvpe;
                }
            }
        }
        public bool UseCrepe {
            get => SelectedMethod == PitchExtractionMethod.Crepe;
            set {
                if (value) {
                    SelectedMethod = PitchExtractionMethod.Crepe;
                }
            }
        }
        public bool UseWorldDio {
            get => SelectedMethod == PitchExtractionMethod.WorldDio;
            set {
                if (value) {
                    SelectedMethod = PitchExtractionMethod.WorldDio;
                }
            }
        }
        public bool UseWorldHarvest {
            get => SelectedMethod == PitchExtractionMethod.WorldHarvest;
            set {
                if (value) {
                    SelectedMethod = PitchExtractionMethod.WorldHarvest;
                }
            }
        }
        public bool UseWorldPyin {
            get => SelectedMethod == PitchExtractionMethod.WorldPyin;
            set {
                if (value) {
                    SelectedMethod = PitchExtractionMethod.WorldPyin;
                }
            }
        }

        // Tooltip messages — null when available so no tooltip pops up
        public string? RmvpeNotFoundTip => RmvpeAvailable
            ? null
            : ThemeManager.GetString("dialogs.transcribe.rmvpe.notfound");
        public string? CrepeNotFoundTip => null; // Always available (embedded model)
        public string? WorldDioNotFoundTip => null;
        public string? WorldHarvestNotFoundTip => null;
        public string? WorldPyinNotFoundTip => null;

        public bool NoneAvailable => !RmvpeAvailable && !CrepeAvailable && !WorldDioAvailable;

        public bool CanRun =>
            SelectedMethod == PitchExtractionMethod.Rmvpe && RmvpeAvailable ||
            SelectedMethod == PitchExtractionMethod.Crepe ||
            SelectedMethod == PitchExtractionMethod.WorldDio ||
            SelectedMethod == PitchExtractionMethod.WorldHarvest ||
            SelectedMethod == PitchExtractionMethod.WorldPyin;

        public ApplyPitchViewModel() {
            RmvpeAvailable = RmvpeTranscriber.IsInstalled();
            CrepeAvailable = true; // Embedded model, always available
            WorldDioAvailable = true;
            WorldHarvestAvailable = true;
            WorldPyinAvailable = true;

            // Default to RMVPE if available, otherwise CREPE, otherwise WORLD DIO
            if (RmvpeAvailable) {
                SelectedMethod = PitchExtractionMethod.Rmvpe;
            } else if (CrepeAvailable) {
                SelectedMethod = PitchExtractionMethod.Crepe;
            } else {
                SelectedMethod = PitchExtractionMethod.WorldDio;
            }

            // Propagate SelectedMethod changes to derived properties
            this.WhenAnyValue(vm => vm.SelectedMethod)
                .Subscribe(_ => {
                    this.RaisePropertyChanged(nameof(UseRmvpe));
                    this.RaisePropertyChanged(nameof(UseCrepe));
                    this.RaisePropertyChanged(nameof(UseWorldDio));
                    this.RaisePropertyChanged(nameof(UseWorldHarvest));
                    this.RaisePropertyChanged(nameof(UseWorldPyin));
                    this.RaisePropertyChanged(nameof(CanRun));
                });
        }
    }
}
