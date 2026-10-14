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
        private ReferenceLapIndex? _referenceLap;
        private LiveDeltaEngine? _deltaEngine;
        private SharedMemoryListener? _sharedMemoryListener;

        private CancellationTokenSource? _simulationCts;
        private Task? _simulationTask;

        // Observable State Backing Fields
        private string _connectionStatus = "STANDBY";
        private string _statusMessage = "Ready. Load baseline or start live ingestion.";
        private bool _isLiveConnected;
        private bool _isSimulating;

        private string _referenceTrackName = "Monza (Autodromo Nazionale Monza)";
        private string _referenceDriverName = "Lewis Hamilton";
        private string _referenceSession = "2023 Italian Grand Prix - Qualifying (Q)";
        private string _referenceLapTimeString = "01:20.294";
        private int _referenceTrackLengthMeters = 5793;

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

        public WorkbenchViewModel(ReferenceLapIndex? referenceLap = null)
        {
            _circularBuffer = new CircularBuffer<LiveDeltaSnapshot>(_bufferCapacity);

            ConnectLiveCommand = new RelayCommand(StartLiveIngestion, () => !IsLiveConnected && !IsSimulating);
            DisconnectLiveCommand = new RelayCommand(StopLiveIngestion, () => IsLiveConnected);
            StartSimulationCommand = new RelayCommand(StartSimulationReplay, () => !IsLiveConnected && !IsSimulating);
            StopSimulationCommand = new RelayCommand(StopSimulationReplay, () => IsSimulating);
            ResetLapCommand = new RelayCommand(ResetLap);

            if (referenceLap != null)
            {
                InitializeReference(referenceLap);
            }
            else
            {
                TryLoadDefaultReferenceLap();
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
                }
            }
        }

        public bool CanToggleLive => !IsSimulating;

        public string ReferenceTrackName
        {
            get => _referenceTrackName;
            set => SetProperty(ref _referenceTrackName, value);
        }

        public string ReferenceDriverName
        {
            get => _referenceDriverName;
            set => SetProperty(ref _referenceDriverName, value);
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
        }

        /// <summary>
        /// Attempts to locate and load the default monza_reference_lap.parquet from disk.
        /// </summary>
        public bool TryLoadDefaultReferenceLap()
        {
            string? filePath = FindReferenceParquetPath();
            if (filePath != null && File.Exists(filePath))
            {
                try
                {
                    var lap = ReferenceLapIndex.FromParquetFile(filePath);
                    InitializeReference(lap);
                    return true;
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Failed to parse default Parquet baseline: {ex.Message}";
                    return false;
                }
            }

            StatusMessage = "Reference lap file not found. Place 'monza_reference_lap.parquet' in the root.";
            return false;
        }

        private static string? FindReferenceParquetPath()
        {
            string[] searchPaths = [
                "monza_reference_lap.parquet",
                "../monza_reference_lap.parquet",
                "../../monza_reference_lap.parquet",
                "../../../monza_reference_lap.parquet",
                Path.Combine(AppContext.BaseDirectory, "monza_reference_lap.parquet"),
                Path.Combine(AppContext.BaseDirectory, "../../../../monza_reference_lap.parquet")
            ];

            foreach (var path in searchPaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            return null;
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

            if (_referenceLap == null && !TryLoadDefaultReferenceLap())
            {
                StatusMessage = "Cannot start live ingestion without a valid reference lap loaded.";
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

            if (_referenceLap == null && !TryLoadDefaultReferenceLap())
            {
                StatusMessage = "Cannot run simulation without a valid reference lap baseline.";
                return;
            }

            _simulationCts = new CancellationTokenSource();
            CancellationToken token = _simulationCts.Token;

            IsSimulating = true;
            ConnectionStatus = "SIMULATION REPLAY";
            StatusMessage = "Running 60Hz simulated Monza replay vs Lewis Hamilton baseline...";

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
