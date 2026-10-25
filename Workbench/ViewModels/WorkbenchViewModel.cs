using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Engine;
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
        // Pre-allocated upfront to guarantee zero GC heap allocations during live ingestion.
        public const int DefaultBufferCapacity = 1000;

        private readonly CircularBuffer<LiveDeltaSnapshot> _circularBuffer;
        private readonly ReferenceLibrary? _library;
        private ReferenceLapIndex? _referenceLap;
        private string? _loadedLapPath;
        private LiveDeltaEngine? _deltaEngine;
        private SharedMemoryListener? _sharedMemoryListener;

        private CancellationTokenSource? _simulationCts;
        private Task? _simulationTask;

        // Observable State Backing Fields
        private string _connectionStatus = "STANDBY";
        private string _statusMessage = "Ready. Select a reference lap or start live ingestion.";
        private bool _isLiveConnected;
        private bool _isSimulating;

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

        // Live Ingestion Tracking State
        private float _accumulatedDistance;
        private float _accumulatedLapTime;
        private DateTime _lastPacketTime = DateTime.UtcNow;

        /// <summary>
        /// Command to initiate live shared memory connection to Assetto Corsa Competizione.
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
        /// Command to reset current lap progress, distance, and delta accumulator back to start/finish line.
        /// </summary>
        public ICommand ResetLapCommand { get; }

        public CircularBuffer<LiveDeltaSnapshot> CircularBuffer => _circularBuffer;
        public ReferenceLapIndex? ReferenceLap => _referenceLap;
        public LiveDeltaEngine? DeltaEngine => _deltaEngine;
        public ReferenceLibrary? Library => _library;

        /// <summary>
        /// Raised on the UI thread whenever a new reference lap has been loaded, so the trace panes can redraw.
        /// </summary>
        public event Action? ReferenceLapChanged;

        /// <param name="referenceLap">Optional pre-loaded lap (skips auto-selecting from the library).</param>
        /// <param name="library">Reference library; when null the 'references' folder is searched for.</param>
        public WorkbenchViewModel(ReferenceLapIndex? referenceLap = null, ReferenceLibrary? library = null)
        {
            _circularBuffer = new CircularBuffer<LiveDeltaSnapshot>(_bufferCapacity);
            _library = library ?? ReferenceLibrary.Find(AppContext.BaseDirectory, Directory.GetCurrentDirectory());

            ConnectLiveCommand = new RelayCommand(StartLiveIngestion, () => !IsLiveConnected && !IsSimulating);
            DisconnectLiveCommand = new RelayCommand(StopLiveIngestion, () => IsLiveConnected);
            StartSimulationCommand = new RelayCommand(StartSimulationReplay, () => !IsLiveConnected && !IsSimulating);
            StopSimulationCommand = new RelayCommand(StopSimulationReplay, () => IsSimulating);
            ResetLapCommand = new RelayCommand(ResetLap);

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
                }
            }
        }

        public bool CanToggleLive => !IsSimulating;

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
            set => SetProperty(ref _currentSimTimeSeconds, value);
        }

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
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load reference lap '{path}': {ex.Message}";
            }
        }

        #endregion

        #region Ingestion & Calculation Processing

        /// <summary>
        /// Central update handler for receiving a newly computed LiveDeltaSnapshot.
        /// Updates observable properties on the UI thread and ensures buffer synchronization.
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

            BufferCount = _circularBuffer.Count;
        }

        private void HandleLapCompleted()
        {
            _accumulatedDistance = 0f;
            _accumulatedLapTime = 0f;
            StatusMessage = "Lap completed. Crossed start/finish line.";
        }

        #endregion

        #region Live Mode (ACC Shared Memory)

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
                _lastPacketTime = DateTime.UtcNow;
                _accumulatedDistance = 0f;
                _accumulatedLapTime = 0f;

                _sharedMemoryListener.OnTelemetryUpdated += OnLiveTelemetryReceived;
                _sharedMemoryListener.Start();

                IsLiveConnected = true;
                ConnectionStatus = "LIVE ACC (60Hz)";
                StatusMessage = "Listening to ACC Shared Memory (Local\\acpmf_physics)...";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not start ACC Shared Memory listener: {ex.Message}";
                IsLiveConnected = false;
                ConnectionStatus = "STANDBY";
            }
        }

        private void OnLiveTelemetryReceived(AcPhysicsPacket packet)
        {
            if (_deltaEngine == null || _referenceLap == null) return;

            DateTime now = DateTime.UtcNow;
            float dt = (float)(now - _lastPacketTime).TotalSeconds;
            _lastPacketTime = now;

            if (dt <= 0f || dt > 0.5f) dt = 0.016f; // Clamped nominal frame delta

            float speedMps = packet.SpeedKmh / 3.6f;
            _accumulatedDistance += speedMps * dt;
            _accumulatedLapTime += dt;

            if (_accumulatedDistance >= _referenceLap.TrackLengthMeters)
            {
                _accumulatedDistance = 0f;
                _accumulatedLapTime = 0f;
                _deltaEngine.Reset();
            }

            LiveDeltaSnapshot snapshot = _deltaEngine.Process(packet, _accumulatedDistance, _accumulatedLapTime);

            // Dispatch update to UI thread if Application dispatcher is present
            if (Application.Current != null && Application.Current.Dispatcher != null)
            {
                Application.Current.Dispatcher.BeginInvoke(() => UpdateSnapshot(snapshot));
            }
            else
            {
                UpdateSnapshot(snapshot);
            }
        }

        public void StopLiveIngestion()
        {
            if (!IsLiveConnected) return;

            if (_sharedMemoryListener != null)
            {
                _sharedMemoryListener.OnTelemetryUpdated -= OnLiveTelemetryReceived;
                _sharedMemoryListener.Stop();
                _sharedMemoryListener.Dispose();
                _sharedMemoryListener = null;
            }

            IsLiveConnected = false;
            ConnectionStatus = "STANDBY";
            StatusMessage = "Live stream disconnected.";
        }

        #endregion

        #region Simulation Dry-Run Mode

        public void StartSimulationReplay()
        {
            if (IsLiveConnected || IsSimulating) return;

            if (_referenceLap == null)
            {
                StatusMessage = "Cannot run simulation without a reference lap. Select one first.";
                return;
            }

            _simulationCts = new CancellationTokenSource();
            CancellationToken token = _simulationCts.Token;

            IsSimulating = true;
            ConnectionStatus = "SIMULATION REPLAY";
            StatusMessage = $"Running 60Hz simulated replay vs {ReferenceDriverName} ({ReferenceSession}) baseline...";

            _simulationTask = Task.Run(async () =>
            {
                float simDistance = 0f;
                float simTime = 0f;
                const float dt = 0.01667f; // Fixed 60Hz tick

                while (!token.IsCancellationRequested && _referenceLap != null && _deltaEngine != null)
                {
                    ReferenceSample refSample = _referenceLap.GetSampleAtDistance(simDistance);

                    // Inject slight driver variation: slightly slower in S1, faster in S2
                    float variation = simDistance < 2000f ? 0.985f : 1.01f;
                    float simSpeed = refSample.SpeedKmh * variation;
                    float speedMps = simSpeed / 3.6f;

                    simDistance += speedMps * dt;
                    simTime += dt;

                    if (simDistance >= _referenceLap.TrackLengthMeters)
                    {
                        simDistance = 0f;
                        simTime = 0f;
                        _deltaEngine.Reset();
                    }

                    LiveDeltaSnapshot delta = _deltaEngine.Calculate(
                        simDistance: simDistance,
                        simTime: simTime,
                        simSpeed: simSpeed,
                        simThrottle: refSample.Throttle,
                        simBrake: refSample.Brake,
                        simGear: refSample.Gear,
                        simRpm: refSample.Rpms
                    );

                    _circularBuffer.Push(delta);

                    if (Application.Current != null && Application.Current.Dispatcher != null)
                    {
                        _ = Application.Current.Dispatcher.BeginInvoke(() => UpdateSnapshot(delta));
                    }
                    else
                    {
                        UpdateSnapshot(delta);
                    }

                    await Task.Delay(16, token).ConfigureAwait(false);
                }
            }, token);
        }

        public void StopSimulationReplay()
        {
            if (!IsSimulating) return;

            _simulationCts?.Cancel();
            IsSimulating = false;
            ConnectionStatus = "STANDBY";
            StatusMessage = "Simulation stopped.";
        }

        #endregion

        #region Lap & State Reset

        public void ResetLap()
        {
            _accumulatedDistance = 0f;
            _accumulatedLapTime = 0f;
            _circularBuffer.Clear();
            _deltaEngine?.Reset();

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
