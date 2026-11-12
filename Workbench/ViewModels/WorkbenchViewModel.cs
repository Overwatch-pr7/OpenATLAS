using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Engine;
using OpenATLAS.Core.Import;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Network;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// OpenATLAS Phase 4 - Task 4.1: UI Shell ViewModel
    /// Primary view model orchestrating the trackside telemetry workbench state,
    /// integrating the underlying Core ingestion worker, circular ring buffer,
    /// and delta calculation engine into thread-safe observable properties for WPF.
    /// </summary>
    public class WorkbenchViewModel : ViewModelBase, IDisposable
    {
        // Default circular buffer capacity for high-frequency 60Hz delta snapshots.
        // 10,000 samples is ~166 s at 60 Hz, enough to hold a full lap for the live traces.
        // Pre-allocated upfront to guarantee zero GC heap allocations during live ingestion.
        public const int DefaultBufferCapacity = 10_000;

        private readonly CircularBuffer<LiveDeltaSnapshot> _circularBuffer;
        private readonly ReferenceLibrary? _library;
        private ReferenceLapIndex? _referenceLap;
        private string? _loadedLapPath;
        private LiveDeltaEngine? _deltaEngine;
        private SharedMemoryListener? _sharedMemoryListener;

        // UI thread context (captured in the constructor) for updates coming from background threads
        private readonly SynchronizationContext? _uiContext;

        private CancellationTokenSource? _simulationCts;
        private Task? _simulationTask;

        // Observable State Backing Fields
        private string _connectionStatus = "STANDBY";
        private string _statusMessage = "Ready. Select a reference lap or start live ingestion.";
        private bool _isLiveConnected;
        private bool _isSimulating;

        // Set from the UI thread, read by the simulation task every tick
        private volatile bool _isSimulationPaused;

        // Bottom status bar "SOURCE:" text: where the live data currently comes from
        public const string SourceNone = "N/A";
        public const string SourceSimulation = "SIMULATED REPLAY OF REFERENCE LAP";
        public const string SourceSavedLap = "SAVED LAP";
        public const string SourceAssettoCorsa = @"ASSETTO CORSA SHARED MEMORY (Local\acpmf_*)";
        private string _dataSource = SourceNone;

        // Racer 1: where the compared lap comes from. Racer 2 is the reference (Year/Grand Prix/Driver).
        public const string Racer1LiveAc = "LIVE AC";
        public const string Racer1Simulation = "SIMULATION";
        public const string Racer1SavedLap = "SAVED LAP";
        public static IReadOnlyList<string> Racer1Sources { get; } = [Racer1LiveAc, Racer1Simulation, Racer1SavedLap];
        private string _selectedRacer1Source = Racer1Simulation;
        private IReadOnlyList<string> _availableSavedLaps = [];
        private string? _selectedSavedLap;

        // Status shown while a playback (simulation or saved lap) runs / is paused
        private string _playbackStatus = "";
        private string _playbackPausedStatus = "";
        private string _playbackMessage = "";

        // Live AC lap recording into My Laps (recorder is used on the listener thread; lock it)
        private readonly LapRecorder _recorder = new();
        private volatile bool _isRecordingLaps;
        private int _lastCompletedLaps = -1;
        private int _recordedLapNumber;

        private string _referenceTrackName = "-";
        private string _referenceDriverName = "-";
        private string _referenceSession = "-";
        private string _referenceLapTimeString = "--:--.---";
        private int _referenceTrackLengthMeters;

        // Reference Library Selection (Year -> Track -> Driver)
        private IReadOnlyList<string> _availableYears = [];
        private IReadOnlyList<string> _availableTracks = [];
        private IReadOnlyList<string> _availableDrivers = [];
        private string? _selectedYear;
        private string? _selectedTrack;
        private string? _selectedDriver;

        private float _currentDistanceMeters;
        private float _currentSimSpeedKmh;
        private float _currentRefSpeedKmh;
        private float _speedDeltaKmh;

        private float _currentSimThrottlePercent;
        private float _currentRefThrottlePercent;
        private float _currentSimBrakePercent;
        private float _currentRefBrakePercent;

        private int _currentGear;
        private int _currentRpm;

        private float _currentSimTimeSeconds;
        private float _currentRefTimeSeconds;
        private float _currentDeltaSeconds;
        private bool _isAheadOfReference;
        private string _deltaDisplayString = "+0.000s";
        private string _deltaStatusString = "LEVEL";

        private int _bufferCount;
        private int _bufferCapacity = DefaultBufferCapacity;

        // Sector analysis: index of the sector the car is in (-1 = none yet) and the
        // overall delta when it entered that sector
        private int _activeSector = -1;
        private float _sectorStartDelta;

        // Live Ingestion Tracking State: lap distance and time from AC's own lap position and clock
        private AcLapTracker? _acLap;
        private AcLapState _lastAcLapState = AcLapState.Paused;

        /// <summary>
        /// Command to initiate live shared memory connection to Assetto Corsa.
        /// </summary>
        public ICommand ConnectLiveCommand { get; }

        /// <summary>
        /// Command to disconnect from the live simulator stream.
        /// </summary>
        public ICommand DisconnectLiveCommand { get; }

        /// <summary>
        /// Command to launch an internal 60Hz simulated dry-run of the Monza lap.
        /// Useful for testing UI render loops and trace alignment without the simulator running.
        /// </summary>
        public ICommand StartSimulationCommand { get; }

        /// <summary>
        /// Command to halt an active simulated lap run.
        /// </summary>
        public ICommand StopSimulationCommand { get; }

        /// <summary>
        /// Command to pause a running simulated lap, or resume it from exactly where it stopped.
        /// </summary>
        public ICommand PauseSimulationCommand { get; }

        /// <summary>
        /// Command to reset current lap progress, distance, and delta accumulator back to start/finish line.
        /// </summary>
        public ICommand ResetLapCommand { get; }

        /// <summary>Shows or hides the track map section in the sidebar.</summary>
        public ICommand ToggleTrackMapCommand { get; }

        /// <summary>START: begins Racer 1 from the selected source (live AC, simulation or saved lap).</summary>
        public ICommand StartRacer1Command { get; }

        /// <summary>STOP: ends whatever Racer 1 is running.</summary>
        public ICommand StopRacer1Command { get; }

        /// <summary>START when stopped, STOP when running (floating widget and its hotkey).</summary>
        public ICommand ToggleRacer1Command { get; }

        /// <summary>Saves the most recently captured lap under its suggested name (floating widget and its hotkey).</summary>
        public ICommand SaveLastRecordedLapCommand { get; }

        /// <summary>Turns capturing of completed live AC laps on or off.</summary>
        public ICommand ToggleRecordingCommand { get; }

        /// <summary>
        /// Complete live AC laps captured while REC was on, oldest first. They stay in memory until
        /// saved (SAVE) or discarded, so nothing lands in My Laps without the user choosing it.
        /// </summary>
        public ObservableCollection<RecordedLap> RecordedLaps { get; } = [];

        public bool HasRecordedLaps => RecordedLaps.Count > 0;

        /// <summary>REC button text: "REC OFF", "REC ON", or "REC ON (3)" with laps waiting to be saved.</summary>
        public string RecordButtonText =>
            (IsRecordingLaps ? "REC ON" : "REC OFF") + (RecordedLaps.Count > 0 ? $" ({RecordedLaps.Count})" : "");

        public CircularBuffer<LiveDeltaSnapshot> CircularBuffer => _circularBuffer;

        /// <summary>Sector analysis rows for the selected Grand Prix (empty when it has no track.json).</summary>
        public ObservableCollection<SectorViewModel> Sectors { get; } = [];

        public bool HasSectors => Sectors.Count > 0;

        // Track map sizing: the sidebar's inner width, and the height range a map may take
        public const double TrackMapWidth = 284;
        public const double TrackMapMinHeight = 140;
        public const double TrackMapMaxHeight = 320;

        private TrackLayout? _trackLayout;
        private bool _isTrackMapVisible = true;
        private double _trackMapHeight = TrackMapWidth;

        /// <summary>Sectors, corners and map outline of the selected Grand Prix (null without a track.json).</summary>
        public TrackLayout? TrackLayout
        {
            get => _trackLayout;
            private set
            {
                if (!SetProperty(ref _trackLayout, value)) return;
                OnPropertyChanged(nameof(HasTrackMap));
                TrackMapHeight = MapHeightFor(value);
            }
        }

        public bool HasTrackMap => _trackLayout?.HasOutline == true;

        /// <summary>Shown/hidden with the HIDE/SHOW button on the TRACK MAP header.</summary>
        public bool IsTrackMapVisible
        {
            get => _isTrackMapVisible;
            set => SetProperty(ref _isTrackMapVisible, value);
        }

        /// <summary>
        /// Map height for the current track: the full sidebar width times the track's
        /// height/width ratio, within limits so tall tracks don't push everything else away.
        /// Computed per track, so tracks added later need no layout changes.
        /// </summary>
        public double TrackMapHeight
        {
            get => _trackMapHeight;
            private set => SetProperty(ref _trackMapHeight, value);
        }

        public static double MapHeightFor(TrackLayout? layout)
        {
            if (layout == null || !layout.HasOutline) return TrackMapMinHeight;
            return Math.Clamp(TrackMapWidth * layout.MapAspectRatio, TrackMapMinHeight, TrackMapMaxHeight);
        }

        /// <summary>Distance cursor readout shown in the trace pane headers.</summary>
        public CursorReadoutViewModel Cursor { get; } = new();
        public ReferenceLapIndex? ReferenceLap => _referenceLap;
        public LiveDeltaEngine? DeltaEngine => _deltaEngine;
        public ReferenceLibrary? Library => _library;

        /// <summary>
        /// Raised on the UI thread whenever a new reference lap has been loaded, so the trace panes can redraw.
        /// </summary>
        public event Action? ReferenceLapChanged;

        /// <summary>
        /// Raised after RESET LAP (and any other lap reset) has cleared the live data,
        /// so the view can drop anything tied to the old lap, such as cursor pins.
        /// </summary>
        public event Action? LapReset;

        /// <param name="referenceLap">Optional pre-loaded lap (skips auto-selecting from the library).</param>
        /// <param name="library">Reference library; when null the 'references' folder is searched for.</param>
        public WorkbenchViewModel(ReferenceLapIndex? referenceLap = null, ReferenceLibrary? library = null)
        {
            _uiContext = SynchronizationContext.Current;
            _circularBuffer = new CircularBuffer<LiveDeltaSnapshot>(_bufferCapacity);
            _library = library ?? ReferenceLibrary.Find(AppContext.BaseDirectory, Directory.GetCurrentDirectory());

            ConnectLiveCommand = new RelayCommand(StartLiveIngestion, () => !IsLiveConnected && !IsSimulating);
            DisconnectLiveCommand = new RelayCommand(StopLiveIngestion, () => IsLiveConnected);
            StartSimulationCommand = new RelayCommand(StartSimulationReplay, () => !IsLiveConnected && !IsSimulating);
            StopSimulationCommand = new RelayCommand(StopSimulationReplay, () => IsSimulating);
            PauseSimulationCommand = new RelayCommand(TogglePauseSimulation, () => IsSimulating);
            ResetLapCommand = new RelayCommand(ResetLap);
            ToggleTrackMapCommand = new RelayCommand(() => IsTrackMapVisible = !IsTrackMapVisible);
            StartRacer1Command = new RelayCommand(StartRacer1, () => !IsRunning && (!IsSavedLapSource || SelectedSavedLap != null));
            StopRacer1Command = new RelayCommand(StopRacer1, () => IsRunning);
            ToggleRacer1Command = new RelayCommand(ToggleRacer1, () => IsRunning || StartRacer1Command.CanExecute(null));
            SaveLastRecordedLapCommand = new RelayCommand(() => SaveLastRecordedLap(), () => HasRecordedLaps);
            ToggleRecordingCommand = new RelayCommand(() => IsRecordingLaps = !IsRecordingLaps);
            RecordedLaps.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasRecordedLaps));
                OnPropertyChanged(nameof(RecordButtonText));
            };

            AvailableYears = _library?.GetYears() ?? [];

            if (referenceLap != null)
            {
                InitializeReference(referenceLap);
            }
            else if (AvailableYears.Count > 0)
            {
                // Cascades into track, driver and finally loads the lap
                SelectedYear = AvailableYears[0];
            }
            else
            {
                StatusMessage = "No reference laps found. Run extract_reference.py to populate the 'references' folder.";
            }
        }

        #region Observable Properties

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set => SetProperty(ref _connectionStatus, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsLiveConnected
        {
            get => _isLiveConnected;
            private set
            {
                if (SetProperty(ref _isLiveConnected, value))
                {
                    OnPropertyChanged(nameof(CanToggleLive));
                    OnPropertyChanged(nameof(CanChangeReference));
                    OnPropertyChanged(nameof(IsRunning));
                }
            }
        }

        public bool IsSimulating
        {
            get => _isSimulating;
            private set
            {
                if (SetProperty(ref _isSimulating, value))
                {
                    OnPropertyChanged(nameof(CanToggleLive));
                    OnPropertyChanged(nameof(CanChangeReference));
                    OnPropertyChanged(nameof(IsRunning));
                }
            }
        }

        public bool CanToggleLive => !IsSimulating;

        /// <summary>True while Racer 1 is running from any source.</summary>
        public bool IsRunning => IsLiveConnected || IsSimulating;

        public string SelectedRacer1Source
        {
            get => _selectedRacer1Source;
            set
            {
                if (SetProperty(ref _selectedRacer1Source, value)) OnPropertyChanged(nameof(IsSavedLapSource));
            }
        }

        public bool IsSavedLapSource => _selectedRacer1Source == Racer1SavedLap;

        /// <summary>The user's saved laps for the selected Grand Prix (Racer 1 SAVED LAP choices).</summary>
        public IReadOnlyList<string> AvailableSavedLaps
        {
            get => _availableSavedLaps;
            private set => SetProperty(ref _availableSavedLaps, value);
        }

        public string? SelectedSavedLap
        {
            get => _selectedSavedLap;
            set => SetProperty(ref _selectedSavedLap, value);
        }

        /// <summary>Grands Prix a lap can be imported under (every one with F1 reference data).</summary>
        public IReadOnlyList<string> ImportGrandPrixOptions => _library?.GetAllGrandPrix() ?? [];

        /// <summary>Grand Prix currently selected for Racer 2, the default for imports.</summary>
        public string? SelectedGrandPrix => _selectedTrack;

        public bool IsRecordingLaps
        {
            get => _isRecordingLaps;
            set
            {
                if (_isRecordingLaps == value) return;
                _isRecordingLaps = value;
                lock (_recorder)
                {
                    _recorder.Discard();
                    _lastCompletedLaps = -1;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(RecordButtonText));
                StatusMessage = value
                    ? "Recording on: every complete lap from the start/finish line is captured. Press SAVE to keep laps."
                    : RecordedLaps.Count > 0
                        ? $"Recording off. {RecordedLaps.Count} captured lap(s) waiting: press SAVE to keep them."
                        : "Recording off. No complete lap was captured (a lap counts once it is driven line to line).";
            }
        }

        public bool IsSimulationPaused
        {
            get => _isSimulationPaused;
            private set
            {
                if (_isSimulationPaused == value) return;
                _isSimulationPaused = value;
                OnPropertyChanged();
            }
        }

        public string DataSource
        {
            get => _dataSource;
            private set => SetProperty(ref _dataSource, value);
        }

        // The reference is locked while a stream is running so the delta engine is never swapped mid-lap
        public bool CanChangeReference => !IsLiveConnected && !IsSimulating;

        public IReadOnlyList<string> AvailableYears
        {
            get => _availableYears;
            private set => SetProperty(ref _availableYears, value);
        }

        public IReadOnlyList<string> AvailableTracks
        {
            get => _availableTracks;
            private set => SetProperty(ref _availableTracks, value);
        }

        public IReadOnlyList<string> AvailableDrivers
        {
            get => _availableDrivers;
            private set => SetProperty(ref _availableDrivers, value);
        }

        /// <summary>
        /// Selecting a year refreshes the track list, keeping the current track when that year has it.
        /// </summary>
        public string? SelectedYear
        {
            get => _selectedYear;
            set
            {
                if (!SetProperty(ref _selectedYear, value)) return;

                string? previousTrack = _selectedTrack;
                AvailableTracks = value != null && _library != null ? _library.GetTracks(value) : [];
                SetSelectedTrack(PickOrFirst(AvailableTracks, previousTrack), force: true);
            }
        }

        /// <summary>
        /// Selecting a track refreshes the driver list, keeping the current driver when they have a lap there.
        /// </summary>
        public string? SelectedTrack
        {
            get => _selectedTrack;
            set => SetSelectedTrack(value, force: false);
        }

        /// <summary>
        /// Selecting a driver loads their reference lap.
        /// </summary>
        public string? SelectedDriver
        {
            get => _selectedDriver;
            set
            {
                if (SetProperty(ref _selectedDriver, value))
                {
                    LoadSelectedReference();
                }
            }
        }

        public string ReferenceLabel => $"{ReferenceDriverName} {SelectedYear} REF";

        public string ReferenceTrackName
        {
            get => _referenceTrackName;
            set => SetProperty(ref _referenceTrackName, value);
        }

        public string ReferenceDriverName
        {
            get => _referenceDriverName;
            set
            {
                if (SetProperty(ref _referenceDriverName, value))
                {
                    OnPropertyChanged(nameof(ReferenceLabel));
                }
            }
        }

        public string ReferenceSession
        {
            get => _referenceSession;
            set => SetProperty(ref _referenceSession, value);
        }

        public string ReferenceLapTimeString
        {
            get => _referenceLapTimeString;
            set => SetProperty(ref _referenceLapTimeString, value);
        }

        public int ReferenceTrackLengthMeters
        {
            get => _referenceTrackLengthMeters;
            set => SetProperty(ref _referenceTrackLengthMeters, value);
        }

        public float CurrentDistanceMeters
        {
            get => _currentDistanceMeters;
            set => SetProperty(ref _currentDistanceMeters, value);
        }

        public float CurrentSimSpeedKmh
        {
            get => _currentSimSpeedKmh;
            set => SetProperty(ref _currentSimSpeedKmh, value);
        }

        public float CurrentRefSpeedKmh
        {
            get => _currentRefSpeedKmh;
            set => SetProperty(ref _currentRefSpeedKmh, value);
        }

        public float SpeedDeltaKmh
        {
            get => _speedDeltaKmh;
            set => SetProperty(ref _speedDeltaKmh, value);
        }

        public float CurrentSimThrottlePercent
        {
            get => _currentSimThrottlePercent;
            set => SetProperty(ref _currentSimThrottlePercent, value);
        }

        public float CurrentRefThrottlePercent
        {
            get => _currentRefThrottlePercent;
            set => SetProperty(ref _currentRefThrottlePercent, value);
        }

        public float CurrentSimBrakePercent
        {
            get => _currentSimBrakePercent;
            set => SetProperty(ref _currentSimBrakePercent, value);
        }

        public float CurrentRefBrakePercent
        {
            get => _currentRefBrakePercent;
            set => SetProperty(ref _currentRefBrakePercent, value);
        }

        public int CurrentGear
        {
            get => _currentGear;
            set => SetProperty(ref _currentGear, value);
        }

        public int CurrentRpm
        {
            get => _currentRpm;
            set => SetProperty(ref _currentRpm, value);
        }

        public float CurrentSimTimeSeconds
        {
            get => _currentSimTimeSeconds;
            set
            {
                if (SetProperty(ref _currentSimTimeSeconds, value)) OnPropertyChanged(nameof(CurrentLapTimeString));
            }
        }

        /// <summary>Racer 1's current lap time as m:ss.fff (floating widget).</summary>
        public string CurrentLapTimeString => TimeSpan.FromSeconds(Math.Max(0, _currentSimTimeSeconds)).ToString(@"m\:ss\.fff");

        public float CurrentRefTimeSeconds
        {
            get => _currentRefTimeSeconds;
            set => SetProperty(ref _currentRefTimeSeconds, value);
        }

        public float CurrentDeltaSeconds
        {
            get => _currentDeltaSeconds;
            set => SetProperty(ref _currentDeltaSeconds, value);
        }

        public bool IsAheadOfReference
        {
            get => _isAheadOfReference;
            set => SetProperty(ref _isAheadOfReference, value);
        }

        public string DeltaDisplayString
        {
            get => _deltaDisplayString;
            set => SetProperty(ref _deltaDisplayString, value);
        }

        public string DeltaStatusString
        {
            get => _deltaStatusString;
            set => SetProperty(ref _deltaStatusString, value);
        }

        public int BufferCount
        {
            get => _bufferCount;
            set => SetProperty(ref _bufferCount, value);
        }

        public int BufferCapacity
        {
            get => _bufferCapacity;
            set => SetProperty(ref _bufferCapacity, value);
        }

        #endregion

        #region Core Initialization

        /// <summary>
        /// Binds a parsed reference lap into the view model and configures the delta calculation engine.
        /// </summary>
        public void InitializeReference(ReferenceLapIndex referenceLap)
        {
            ArgumentNullException.ThrowIfNull(referenceLap);
            _referenceLap = referenceLap;
            _referenceTrackLengthMeters = referenceLap.TrackLengthMeters;
            _referenceLapTimeString = TimeSpan.FromSeconds(referenceLap.TotalLapTimeSeconds).ToString("mm\\:ss\\.fff");

            OnPropertyChanged(nameof(ReferenceTrackLengthMeters));
            OnPropertyChanged(nameof(ReferenceLapTimeString));

            _deltaEngine = new LiveDeltaEngine(_referenceLap, _circularBuffer);
            _deltaEngine.OnLapCompleted += HandleLapCompleted;

            StatusMessage = $"Baseline loaded: {referenceLap.SampleCount} samples over {referenceLap.TrackLengthMeters}m ({_referenceLapTimeString}).";
            ReferenceLapChanged?.Invoke();
        }

        private void SetSelectedTrack(string? value, bool force)
        {
            if (!SetProperty(ref _selectedTrack, value, nameof(SelectedTrack)) && !force) return;

            string? previousDriver = _selectedDriver;
            AvailableDrivers = value != null && _selectedYear != null && _library != null
                ? _library.GetDrivers(_selectedYear, value)
                : [];
            RefreshSavedLaps();

            string? driver = PickOrFirst(AvailableDrivers, previousDriver);
            if (driver == _selectedDriver)
            {
                // Same driver code but a different year/track: still a different lap file
                LoadSelectedReference();
            }
            else
            {
                SelectedDriver = driver;
            }
        }

        private static string? PickOrFirst(IReadOnlyList<string> options, string? preferred)
        {
            if (preferred != null && options.Contains(preferred)) return preferred;
            return options.Count > 0 ? options[0] : null;
        }

        /// <summary>
        /// Loads the lap for the current Year/Track/Driver selection and resets the live session against it.
        /// </summary>
        private void LoadSelectedReference()
        {
            if (_library == null || _selectedYear == null || _selectedTrack == null || _selectedDriver == null) return;

            string path = _library.GetLapPath(_selectedYear, _selectedTrack, _selectedDriver);
            if (path == _loadedLapPath) return;

            try
            {
                var lap = ReferenceLapIndex.FromParquetFile(path);
                _loadedLapPath = path;

                ResetLap();
                ReferenceTrackName = _selectedTrack;
                ReferenceDriverName = _selectedDriver;
                ReferenceSession = $"{_selectedYear} {_selectedTrack}";
                OnPropertyChanged(nameof(ReferenceLabel));
                InitializeReference(lap);
                LoadSectors(lap);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load reference lap '{path}': {ex.Message}";
            }
        }

        /// <summary>
        /// Rebuilds the sector analysis rows from the Grand Prix's track.json.
        /// A missing or broken layout only empties the panel; the lap itself stays loaded.
        /// </summary>
        private void LoadSectors(ReferenceLapIndex lap)
        {
            Sectors.Clear();
            _activeSector = -1;
            TrackLayout? layout = null;

            try
            {
                layout = _library?.GetTrackLayout(_selectedYear!, _selectedTrack!);
                if (layout != null)
                {
                    foreach (TrackSector sector in layout.Sectors)
                    {
                        float refSeconds = lap.GetTimeAtDistance(sector.EndMeters) - lap.GetTimeAtDistance(sector.StartMeters);
                        Sectors.Add(new SectorViewModel(sector, layout.CornersIn(sector), refSeconds));
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lap loaded, but its track.json could not be read: {ex.Message}";
            }

            OnPropertyChanged(nameof(HasSectors));
            TrackLayout = layout;
        }

        /// <summary>
        /// Live sector deltas: time gained/lost in a sector is the change in the overall delta
        /// between entering and leaving it. Sectors already passed keep their final value.
        /// </summary>
        private void UpdateSectorDeltas(in LiveDeltaSnapshot snapshot)
        {
            if (Sectors.Count == 0) return;

            int sector = SectorIndexAt(snapshot.Distance);

            // First sample, or the distance jumped back to an earlier sector: a new lap began.
            // Joining mid-lap leaves the sectors already driven empty rather than guessing them.
            if (_activeSector < 0 || sector < _activeSector)
            {
                ClearSectorDeltas();
                _activeSector = sector;
                _sectorStartDelta = sector == 0 ? 0f : snapshot.DeltaTime;
            }

            // Close every sector boundary crossed since the last frame
            while (_activeSector < sector)
            {
                Sectors[_activeSector].SetDelta(snapshot.DeltaTime - _sectorStartDelta);
                Sectors[_activeSector].IsActive = false;
                _sectorStartDelta = snapshot.DeltaTime;
                _activeSector++;
            }

            Sectors[sector].IsActive = true;
            Sectors[sector].SetDelta(snapshot.DeltaTime - _sectorStartDelta);
        }

        private int SectorIndexAt(float distanceMeters)
        {
            for (int i = 0; i < Sectors.Count; i++)
            {
                if (Sectors[i].ContainsDistance(distanceMeters)) return i;
            }

            // Past the last boundary (sim track slightly longer than the reference): still the final sector
            return distanceMeters <= 0f ? 0 : Sectors.Count - 1;
        }

        private void ClearSectorDeltas()
        {
            foreach (SectorViewModel row in Sectors) row.ClearDelta();
            _activeSector = -1;
            _sectorStartDelta = 0f;
        }

        #endregion

        #region Ingestion & Calculation Processing

        /// <summary>
        /// Pushes a snapshot's values into the bound sidebar properties.
        /// Called on the UI thread by the 60 Hz render loop (Task 4.3), never per packet.
        /// </summary>
        public void UpdateSnapshot(in LiveDeltaSnapshot snapshot)
        {
            // Update bound properties
            CurrentDistanceMeters = snapshot.Distance;
            CurrentSimSpeedKmh = snapshot.SimSpeed;
            CurrentRefSpeedKmh = snapshot.RefSpeed;
            SpeedDeltaKmh = snapshot.SimSpeed - snapshot.RefSpeed;

            CurrentSimThrottlePercent = snapshot.SimThrottle;
            CurrentRefThrottlePercent = snapshot.RefThrottle;
            CurrentSimBrakePercent = snapshot.SimBrake;
            CurrentRefBrakePercent = snapshot.RefBrake;

            CurrentGear = snapshot.SimGear;
            CurrentRpm = snapshot.SimRpm;

            CurrentSimTimeSeconds = snapshot.SimTime;
            CurrentRefTimeSeconds = snapshot.RefTime;
            CurrentDeltaSeconds = snapshot.DeltaTime;

            // Formatted delta string (+0.000s or -0.185s)
            bool ahead = snapshot.DeltaTime >= 0f;
            IsAheadOfReference = ahead;
            string sign = ahead ? "+" : "-";
            DeltaDisplayString = $"{sign}{MathF.Abs(snapshot.DeltaTime):F3}s";
            DeltaStatusString = MathF.Abs(snapshot.DeltaTime) < 0.005f ? "LEVEL" : (ahead ? "FASTER" : "SLOWER");

            UpdateSectorDeltas(snapshot);

            BufferCount = _circularBuffer.Count;
        }

        // Runs the action on the UI thread (or directly when there is no UI, e.g. in tests)
        private void RunOnUiThread(Action action)
        {
            if (_uiContext != null) _uiContext.Post(_ => action(), null);
            else action();
        }

        private void HandleLapCompleted()
        {
            RunOnUiThread(() => StatusMessage = "Lap completed. Crossed start/finish line.");
        }

        #endregion

        #region Live Mode (Assetto Corsa Shared Memory)

        public void StartLiveIngestion()
        {
            if (IsLiveConnected || IsSimulating) return;

            if (_referenceLap == null)
            {
                StatusMessage = "Cannot start live ingestion without a reference lap. Select one first.";
                return;
            }

            try
            {
                _sharedMemoryListener = new SharedMemoryListener();
                _acLap = new AcLapTracker(_referenceLap.TrackLengthMeters);
                _lastAcLapState = AcLapState.Paused;

                _sharedMemoryListener.OnTelemetryUpdated += OnLiveTelemetryReceived;
                _sharedMemoryListener.OnConnectionChanged += OnAcConnectionChanged;
                _sharedMemoryListener.Start();

                // The listener keeps looking for AC in the background, so this can be clicked before the game starts
                IsLiveConnected = true;
                ConnectionStatus = "WAITING FOR AC";
                StatusMessage = "Waiting for Assetto Corsa... load a session and it will connect automatically.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not start the Assetto Corsa listener: {ex.Message}";
                IsLiveConnected = false;
                ConnectionStatus = "STANDBY";
            }
        }

        // Called on the listener thread when AC's shared memory pages are found
        private void OnAcConnectionChanged(bool connected)
        {
            if (!connected) return; // Disconnect is handled by StopLiveIngestion

            RunOnUiThread(() =>
            {
                ConnectionStatus = "LIVE AC (60Hz)";
                DataSource = SourceAssettoCorsa;
                StatusMessage = "Connected to Assetto Corsa shared memory.";
            });
        }

        // Called on the listener thread for every new AC sample.
        // Distance and lap time come from AC's lap position and lap clock (see AcLapTracker), so
        // nothing drifts over a lap, pauses stop the clock, and out laps are not compared.
        private void OnLiveTelemetryReceived(AcPhysicsPacket packet, AcGraphicsPacket graphics)
        {
            if (_deltaEngine == null || _referenceLap == null || _acLap == null) return;

            AcLapPosition lap = _acLap.Update(graphics);
            ReportAcLapState(_acLap.State);

            if (lap.IsTimed)
            {
                // Process() pushes the snapshot into the ring buffer (engine OutputBuffer).
                // No UI work here: the 60 Hz render loop picks it up (Task 4.3).
                _deltaEngine.Process(packet, lap.DistanceMeters, lap.LapTimeSeconds);
            }

            if (_isRecordingLaps) RecordSample(packet, graphics);
        }

        // Status line on changes only (out lap, paused, timing), not on every sample
        private void ReportAcLapState(AcLapState state)
        {
            if (state == _lastAcLapState) return;
            _lastAcLapState = state;

            string message = state switch
            {
                AcLapState.WaitingForLine => "Out lap: the comparison starts when you cross the start/finish line.",
                AcLapState.Paused => "Assetto Corsa paused or in a menu: the lap clock is stopped.",
                _ => "Timing a lap from the start/finish line."
            };
            RunOnUiThread(() => StatusMessage = message);
        }

        /// <summary>
        /// Recording follows AC's own lap counter and lap clock, so saved laps start and end exactly
        /// on AC's start/finish line. Distance is integrated from speed when the lap is normalized.
        /// </summary>
        private void RecordSample(in AcPhysicsPacket packet, in AcGraphicsPacket graphics)
        {
            if (graphics.Status != AcStatus.Live || _referenceLap == null) return; // menus, replays, pause

            IReadOnlyList<RawLapSample>? finished = null;
            string? rejectReason = null;
            bool lapEnded = false;
            lock (_recorder)
            {
                if (_lastCompletedLaps >= 0 && graphics.CompletedLaps != _lastCompletedLaps)
                {
                    lapEnded = true;
                    finished = _recorder.FinishLap(_referenceLap.TrackLengthMeters, out rejectReason);
                }
                _lastCompletedLaps = graphics.CompletedLaps;

                _recorder.Add(new RawLapSample(
                    graphics.CurrentTimeMs / 1000.0, double.NaN, packet.SpeedKmh,
                    packet.Gas * 100.0, packet.Brake * 100.0, packet.DisplayGear, packet.Rpms));
            }

            if (!lapEnded) return;

            if (finished != null && _selectedTrack is string grandPrix)
            {
                // AC's own lap time; fall back to the recorded clock if AC has not published it yet
                TimeSpan lapTime = graphics.LastTimeMs > 0
                    ? TimeSpan.FromMilliseconds(graphics.LastTimeMs)
                    : TimeSpan.FromSeconds(finished[^1].TimeSeconds);
                RunOnUiThread(() => AddRecordedLap(finished, lapTime, grandPrix));
            }
            else
            {
                RunOnUiThread(() => StatusMessage = $"Lap not captured: {rejectReason}.");
            }
        }

        /// <summary>Adds a completed lap to the session's captured laps (called on the UI thread).</summary>
        public void AddRecordedLap(IReadOnlyList<RawLapSample> samples, TimeSpan lapTime, string grandPrix)
        {
            var lap = new RecordedLap(++_recordedLapNumber, lapTime, grandPrix, DateTime.Now, samples);
            RecordedLaps.Add(lap);
            StatusMessage = $"Lap {lap.Number} captured: {lapTime:m\\:ss\\.fff}. {RecordedLaps.Count} lap(s) waiting: press SAVE to keep them.";
        }

        /// <summary>
        /// Normalizes a captured lap, saves it to My Laps under its Grand Prix and removes it from the
        /// waiting list. Returns the name it was saved under.
        /// </summary>
        public string SaveRecordedLap(RecordedLap lap, string name)
        {
            if (_library == null)
            {
                throw new InvalidOperationException("No 'references' folder was found, so there is nowhere to save laps.");
            }

            string saved = _library.SaveUserLap(LapNormalizer.Normalize(lap.Samples), lap.GrandPrix, name);
            RecordedLaps.Remove(lap);
            RefreshUserLaps();
            StatusMessage = $"Lap saved to My Laps > {lap.GrandPrix} > {saved}.";
            return saved;
        }

        public void DiscardRecordedLaps() => RecordedLaps.Clear();

        /// <summary>
        /// Quick save for use while driving: keeps the most recently captured lap under its suggested
        /// name, without a dialog. Returns the saved name, or null when there was nothing to save.
        /// </summary>
        public string? SaveLastRecordedLap()
        {
            if (RecordedLaps.Count == 0)
            {
                StatusMessage = "No captured lap to save yet: a lap counts once it is driven line to line with REC on.";
                return null;
            }

            RecordedLap last = RecordedLaps[^1];
            try
            {
                return SaveRecordedLap(last, last.DefaultName);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                StatusMessage = $"Could not save lap {last.Number}: {ex.Message}";
                return null;
            }
        }

        public void ToggleRacer1()
        {
            if (IsRunning) StopRacer1();
            else StartRacer1();
        }

        public void StopLiveIngestion()
        {
            if (!IsLiveConnected) return;

            if (_sharedMemoryListener != null)
            {
                _sharedMemoryListener.OnTelemetryUpdated -= OnLiveTelemetryReceived;
                _sharedMemoryListener.OnConnectionChanged -= OnAcConnectionChanged;
                _sharedMemoryListener.Stop();
                _sharedMemoryListener.Dispose();
                _sharedMemoryListener = null;
            }

            lock (_recorder)
            {
                _recorder.Discard();
                _lastCompletedLaps = -1;
            }

            IsLiveConnected = false;
            ConnectionStatus = "STANDBY";
            DataSource = SourceNone;
            StatusMessage = "Live stream disconnected.";
        }

        #endregion

        #region Racer 1 Playback (Simulation & Saved Lap)

        public void StartSimulationReplay()
        {
            if (_referenceLap == null)
            {
                StatusMessage = "Cannot run simulation without a reference lap. Select one first.";
                return;
            }

            StartPlayback(new SyntheticDriver(_referenceLap), "SIMULATION REPLAY", "SIMULATION PAUSED", SourceSimulation,
                $"Running 60Hz simulated replay vs {ReferenceDriverName} ({ReferenceSession}) baseline...");
        }

        /// <summary>Replays the selected saved lap (Racer 1) against the selected reference (Racer 2).</summary>
        public void StartSavedLapReplay()
        {
            if (_referenceLap == null)
            {
                StatusMessage = "Cannot replay a lap without a reference lap. Select one first.";
                return;
            }
            if (_library == null || _selectedTrack == null || SelectedSavedLap == null)
            {
                StatusMessage = "No saved lap selected. Import one (File > Import Lap) or record one live first.";
                return;
            }

            ReferenceLapIndex saved;
            try
            {
                saved = ReferenceLapIndex.FromParquetFile(
                    _library.GetLapPath(ReferenceLibrary.UserLapsFolderName, _selectedTrack, SelectedSavedLap));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not load saved lap '{SelectedSavedLap}': {ex.Message}";
                return;
            }

            StartPlayback(new SavedLapDriver(saved, _referenceLap.TrackLengthMeters), "SAVED LAP REPLAY", "SAVED LAP PAUSED",
                $"{SourceSavedLap}: {SelectedSavedLap}",
                $"Replaying {SelectedSavedLap} vs {ReferenceDriverName} ({ReferenceSession})...");
        }

        // Runs a Racer 1 playback at a fixed 60 Hz: the driver produces a sample, the delta engine
        // compares it with the reference, and the result goes into the ring buffer for the render loop.
        private void StartPlayback(IPlaybackDriver driver, string status, string pausedStatus, string source, string message)
        {
            if (IsLiveConnected || IsSimulating) return;

            _simulationCts = new CancellationTokenSource();
            CancellationToken token = _simulationCts.Token;

            _playbackStatus = status;
            _playbackPausedStatus = pausedStatus;
            _playbackMessage = message;

            IsSimulationPaused = false;
            IsSimulating = true;
            ConnectionStatus = status;
            DataSource = source;
            StatusMessage = message;

            _simulationTask = Task.Run(async () =>
            {
                const float dt = 0.01667f; // Fixed 60Hz tick

                while (!token.IsCancellationRequested && _referenceLap != null && _deltaEngine != null)
                {
                    // Paused: playback time and distance stand still, so resuming continues seamlessly
                    if (_isSimulationPaused)
                    {
                        await Task.Delay(16, token).ConfigureAwait(false);
                        continue;
                    }

                    PlaybackSample sample = driver.Next(dt);
                    if (sample.NewLap) _deltaEngine.Reset();

                    LiveDeltaSnapshot delta = _deltaEngine.Calculate(
                        simDistance: sample.Distance,
                        simTime: sample.Time,
                        simSpeed: sample.Speed,
                        simThrottle: sample.Throttle,
                        simBrake: sample.Brake,
                        simGear: sample.Gear,
                        simRpm: sample.Rpm
                    );

                    // Only feed the ring buffer; the 60 Hz render loop draws it (Task 4.3)
                    _circularBuffer.Push(delta);

                    await Task.Delay(16, token).ConfigureAwait(false);
                }
            }, token);
        }

        public void StopSimulationReplay()
        {
            if (!IsSimulating) return;

            _simulationCts?.Cancel();
            IsSimulationPaused = false;
            IsSimulating = false;
            ConnectionStatus = "STANDBY";
            DataSource = SourceNone;
            StatusMessage = "Playback stopped.";
        }

        public void TogglePauseSimulation()
        {
            if (!IsSimulating) return;

            IsSimulationPaused = !IsSimulationPaused;
            ConnectionStatus = IsSimulationPaused ? _playbackPausedStatus : _playbackStatus;
            StatusMessage = IsSimulationPaused
                ? "Paused. Click RESUME to continue from the same point."
                : _playbackMessage;
        }

        // START: whichever Racer 1 source is selected
        public void StartRacer1()
        {
            switch (SelectedRacer1Source)
            {
                case Racer1LiveAc: StartLiveIngestion(); break;
                case Racer1SavedLap: StartSavedLapReplay(); break;
                default: StartSimulationReplay(); break;
            }
        }

        public void StopRacer1()
        {
            if (IsLiveConnected) StopLiveIngestion();
            if (IsSimulating) StopSimulationReplay();
        }

        #endregion

        #region My Laps (Import & Library)

        /// <summary>
        /// Normalizes an imported lap and saves it to My Laps under the given Grand Prix.
        /// Returns the name it was saved under. Throws when the lap cannot be used.
        /// </summary>
        public string SaveImportedLap(IReadOnlyList<RawLapSample> samples, string grandPrix, string lapName)
        {
            if (_library == null)
            {
                throw new InvalidOperationException("No 'references' folder was found, so there is nowhere to save laps.");
            }

            string saved = _library.SaveUserLap(LapNormalizer.Normalize(samples), grandPrix, lapName);
            RefreshUserLaps();
            if (grandPrix == _selectedTrack) SelectedSavedLap = saved;
            StatusMessage = $"Lap imported to My Laps > {grandPrix} > {saved}.";
            return saved;
        }

        /// <summary>Folder holding the user's laps (created if missing), for File > Open My Laps Folder.</summary>
        public string? UserLapsFolder
        {
            get
            {
                if (_library == null) return null;
                string folder = _library.UserLapsPath;
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        // Picks up newly saved laps in the Racer 1 list, and in the Racer 2 dropdowns when "My Laps"
        // is selected there. Lists are only replaced when their contents changed, so the current
        // selections (and the loaded reference) are left alone.
        private void RefreshUserLaps()
        {
            if (_library == null) return;

            var years = _library.GetYears();
            if (!years.SequenceEqual(AvailableYears)) AvailableYears = years;

            if (_selectedYear == ReferenceLibrary.UserLapsFolderName && _selectedTrack != null)
            {
                var drivers = _library.GetDrivers(_selectedYear, _selectedTrack);
                if (!drivers.SequenceEqual(AvailableDrivers)) AvailableDrivers = drivers;
            }

            RefreshSavedLaps();
        }

        /// <summary>Loads one of the user's saved laps for the selected Grand Prix. Throws when it cannot be read.</summary>
        public ReferenceLapIndex LoadSavedLap(string name)
        {
            if (_library == null || _selectedTrack == null)
            {
                throw new InvalidOperationException("No Grand Prix is selected.");
            }
            return ReferenceLapIndex.FromParquetFile(
                _library.GetLapPath(ReferenceLibrary.UserLapsFolderName, _selectedTrack, name));
        }

        private void RefreshSavedLaps()
        {
            var laps = _library != null && _selectedTrack != null ? _library.GetUserLaps(_selectedTrack) : [];
            if (!laps.SequenceEqual(AvailableSavedLaps)) AvailableSavedLaps = laps;
            SelectedSavedLap = PickOrFirst(AvailableSavedLaps, SelectedSavedLap);
        }

        #endregion

        #region Lap & State Reset

        public void ResetLap()
        {
            _circularBuffer.Clear();
            _deltaEngine?.Reset();
            ClearSectorDeltas();

            CurrentDistanceMeters = 0f;
            CurrentSimSpeedKmh = 0f;
            CurrentRefSpeedKmh = 0f;
            SpeedDeltaKmh = 0f;
            CurrentSimThrottlePercent = 0f;
            CurrentRefThrottlePercent = 0f;
            CurrentSimBrakePercent = 0f;
            CurrentRefBrakePercent = 0f;
            CurrentGear = 0;
            CurrentRpm = 0;
            CurrentSimTimeSeconds = 0f;
            CurrentRefTimeSeconds = 0f;
            CurrentDeltaSeconds = 0f;
            IsAheadOfReference = true;
            DeltaDisplayString = "+0.000s";
            DeltaStatusString = "LEVEL";
            BufferCount = 0;

            StatusMessage = "Telemetry and delta counters reset to zero.";

            LapReset?.Invoke();
        }

        #endregion

        public void Dispose()
        {
            StopSimulationReplay();
            StopLiveIngestion();
            _simulationCts?.Dispose();
        }
    }
}
