#region File Information
/********************************************************************
  Project: ServiceBusMQ
  File:    SbmqSystem.cs
  Created: 2012-11-27

  Author(s):
    Daniel Halan

 (C) Copyright 2012 Ingenious Technology with Quality Sweden AB
     all rights reserved

********************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NLog;
using ServiceBusMQ.Configuration;
using ServiceBusMQ.Manager;
using ServiceBusMQ.Model;
using ServiceBusMQ.ViewModel;

namespace ServiceBusMQ {

  public enum ItemChangeOrigin { Queue, Filter }
  public class ItemsChangedEventArgs : EventArgs {

    public ItemChangeOrigin Origin { get; set; }

  }

  public class SbmqSystem {

    private class ServiceBusInfo {
      public string Name { get; set; }
      public string Version { get; set; }
      public string QueueType { get; set; }

      public static ServiceBusInfo Create(string name, string v, string qt) {
        return new ServiceBusInfo() {
          Name = name,
          Version = v,
          QueueType = qt
        };
      }
    }

    protected Logger _log = LogManager.GetCurrentClassLogger();

    public static readonly int MAX_ITEMS_PER_QUEUE = 500;

    bool _isServiceBusStarted = false;
    QueueType[] _queueTypeValues;

    IServiceBusManager _mgr;
    CommandHistoryManager _history;
    static UIStateConfig _uiState = new UIStateConfig();
    private SbmqmMonitorState _monitorState;

    List<QueueItemViewModel> _items = new List<QueueItemViewModel>();
    uint[] _unprocessedItemsCount { get; set; }

    public IServiceBusManager Manager { get { return _mgr; } }
    public SystemConfig3 Config { get; private set; }
    public CommandHistoryManager SavedCommands { get { return _history; } }
    public static UIStateConfig UIState { get { return _uiState; } }

    string _filter = null;

    public IEnumerable<QueueItemViewModel> Items {
      get {
        return _filter == null ? _items : _items.Where(i => i.DisplayName.Contains(_filter.Split(' ')));
      }
    }

    public bool CanSendCommand { get; private set; }
    public bool CanViewSubscriptions { get; private set; }

    public static ApplicationInfo AppInfo { get; set; }

    private SbmqSystem() {
      _unprocessedItemsCount = new uint[4];
      _queueTypeValues = (QueueType[])Enum.GetValues(typeof(QueueType));
    }


    private void Initialize() {
      AppDomain.CurrentDomain.AssemblyResolve += SbmqmDomain_AssemblyResolve;
      _monitorState = new SbmqmMonitorState();

      Config = SystemConfig.Load();

      Config.StartCount += 1;
      Config.Save();

      // Check if we still have the ServiceBus Adapter available
      var availMgrs = ServiceBusFactory.AvailableServiceBusManagers();
      if( !availMgrs.Any(mgr => mgr.Name == Config.ServiceBus && mgr.Version == Config.ServiceBusVersion && mgr.QueueType == Config.ServiceBusQueueType) ) {

        // Check if we have any other versions available
        var m = availMgrs.FirstOrDefault(mgr => mgr.Name == Config.ServiceBus && mgr.QueueType == Config.ServiceBusQueueType);

        if( m != null )
          Config.CurrentServer.ServiceBusVersion = m.Version;

        else { // Check if we have this ServiceBus Type
          m = availMgrs.FirstOrDefault(mgr => mgr.Name == Config.ServiceBus);

          if( m != null ) {
            Config.CurrentServer.ServiceBusQueueType = m.QueueType;
            Config.CurrentServer.ServiceBusVersion = m.Version;
          } else throw new NotSupportedException("Not Supported Service Bus type '{0}', Please re-install Service Bus MQ Manager.".With(Config.ServiceBus));


        }


      }

      CreateServiceBusManager(Config.ServiceBus, Config.ServiceBusVersion, Config.ServiceBusQueueType);


      _history = new CommandHistoryManager(Config);

      AppInfo = new ApplicationInfo(Config.Id, Assembly.GetEntryAssembly());
    }

    List<ServiceBusInfo> _serviceBusHistory = new List<ServiceBusInfo>();

    private void CreateServiceBusManager(string serviceBus, string version, string queueType) {

      _mgr = ServiceBusFactory.CreateManager(serviceBus, version, queueType);
      _mgr.ErrorOccured += System_ErrorOccured;
      _mgr.WarningOccured += System_WarningOccured;
      _mgr.ItemsChanged += System_ItemsChanged;

      var cmdSender = ( _mgr as ISendCommand );
      if( cmdSender != null )
        cmdSender.CommandContentFormat = Config.CurrentServer.CommandContentType;

      lock( _itemsLock )
        _items.Clear();

      _mgr.Initialize(Config.CurrentServer.ConnectionSettings, Config.MonitorQueues.Select(mq => new Queue(mq.Name, mq.Type, mq.Color)).ToArray(), _monitorState);

      CanSendCommand = ( _mgr as ISendCommand ) != null;
      CanViewSubscriptions = ( _mgr as IViewSubscriptions ) != null;

      _serviceBusHistory.Add(ServiceBusInfo.Create(serviceBus, version, queueType));

    }

    public void SwitchServiceBus(string serviceBus, string version, string queueType) {
      StopMonitoring();

      _mgr.Terminate();

      if( !_serviceBusHistory.Any(s => s.Name == serviceBus && s.Version != version) ) {

        CreateServiceBusManager(serviceBus, version, queueType);

      } else throw new RestartRequiredException();

      _serviceBusHistory.Add(ServiceBusInfo.Create(serviceBus, version, queueType));
    }



    private static SbmqSystem _instance;
    public static SbmqSystem Create() {
      _instance = new SbmqSystem();
      _instance.Initialize();

      return _instance;
    }


    public IServiceBusDiscovery GetDiscoveryService() {
      return ServiceBusFactory.CreateDiscovery(Config.ServiceBus, Config.ServiceBusVersion, Config.ServiceBusQueueType);
    }
    public IServiceBusDiscovery GetDiscoveryService(string messageBus, string version, string queueType) {
      return ServiceBusFactory.CreateDiscovery(messageBus, version, queueType);
    }


    public Type[] GetAvailableCommands(string messageBus, string version, string queueType, string[] asmPaths, CommandDefinition cmdDef, bool suppressErrors) {
      var mgr = ServiceBusFactory.CreateManager(messageBus, version, queueType) as ISendCommand;

      if( mgr != null ) {
        return mgr.GetAvailableCommands(asmPaths, cmdDef, suppressErrors);

      } else return new Type[0];

    }



    protected volatile object _itemsLock = new object();
    public object ItemsLock { get { return _itemsLock; } }


    volatile ThreadState _currentMonitor = null;
    //bool _monitoring = false;
    public void StartMonitoring() {

      //_monitoring = true;
      _currentMonitor = new ThreadState();
      var t = new Thread(ExecMonitor);
      t.Name = "Queue Monitoring";
      t.Start(_currentMonitor);

    }
    public void StopMonitoring() {
      //_monitoring = false;
      if( _currentMonitor != null ) {
        _currentMonitor.Stop();
        _currentMonitor = null;
      }
    }
    public void PauseMonitoring() {
      if( _currentMonitor != null )
        _currentMonitor.Pause();
    }
    public void ResumeMonitoring() {
      if( _currentMonitor != null )
        _currentMonitor.Resume();
    }


    internal class ThreadState {

      enum State { Stopped = 0, Executing, Paused }

      State _state;

      public bool Executing { get { return _state == State.Executing; } }
      public bool Paused { get { return _state == State.Paused; } }

      public bool Stopped { get { return _state == State.Stopped; } }

      internal ThreadState() {
        _state = State.Executing;
      }

      public void Execute() {
        _state = State.Executing;
      }

      private bool _shouldPause = false;
      public void Pause() {
        _shouldPause = true;
      }
      public void Resume() {
        _state = State.Executing;
      }
      public void Stop() {
        _state = State.Stopped;
      }

      public void Tick() {
        if( _shouldPause ) {
          _state = State.Paused;
        }
      }

    }

    public void ExecMonitor(object prm) {
      var state = prm as ThreadState;

      try {
        while( state.Executing ) {
          state.Tick();

          while( state.Paused )
            Thread.Sleep(1000);

          OnStartedLoadingQueues();
          try {

            if( RefreshUnprocessedQueueItemList() )
              OnItemsChanged(ItemChangeOrigin.Queue);

          } finally {
            OnFinishedLoadingQueues();
          }

          Thread.Sleep(Config.MonitorInterval);
        }

      } finally {
        state.Stop();
      }
    }


    public uint GetUnprocessedItemsCount(QueueType qt) {
      return _unprocessedItemsCount[(int)qt];
    }


    public bool RefreshUnprocessedQueueItemList() {
      _log.Debug("* Refresh Unprocessed Queue Items, " + DateTime.Now.ToString());

      if( !_monitorState.MonitorQueueType.Any(mq => mq) || _mgr.MonitorQueues.Length == 0 )
        return false;

      List<QueueItem> items = new List<QueueItem>();

      // TODO: Solve why we can not iterate thru Remote MQ, 
      // both GetMessageEnumerator2() and GetAllMessages() should be available for
      // Remote computer and direct format name, but returns zero (0) messages in some cases
      //if( !Tools.IsLocalHost(_serverName) )
      //  return;
      bool changedItemsCount = false;

      var monitorStatesWhenFetch = new SbmqmMonitorState(_monitorState.MonitorQueueType);
      List<string> unchangedQueues = new List<string>();

      // Temp until we have a more proper way to Discover if Error queues are built-in with normal queues
      if( !_monitorState.IsMonitoring(QueueType.Error) )
        _unprocessedItemsCount[(int)QueueType.Error] = 0;

      // Removed as when changing QTs in UI this would change list and throw a modification exception in Manager.
      // Creating an array is not as resource efficient, but it works.
      //IEnumerable<QueueItem> currentItems = _items.AsEnumerable<QueueItem>();
      foreach( QueueType t in _queueTypeValues ) {
        if( _monitorState.IsMonitoring(t) ) {
          var r = _mgr.GetUnprocessedMessages(new QueueFetchUnprocessedMessagesRequest(t, _items.ToArray(), GetUnprocessedItemsCount(t)));

          if( r.Status == QueueFetchResultStatus.ConnectionFailed )
            break;

          if( r.Status == QueueFetchResultStatus.NotChanged ) {
            unchangedQueues.AddRange(_mgr.MonitorQueues.Where(q => q.Type == t).Select(q => q.Name));
            continue;
          }

          items.AddRange(r.Items);

          int typeIndex = (int)t;
          if( _unprocessedItemsCount[typeIndex] != r.Count )
            changedItemsCount = true;

          _unprocessedItemsCount[typeIndex] = r.Count;

          if( t != QueueType.Error && items.Any(i => i.Queue.Type == QueueType.Error) ) {
            _unprocessedItemsCount[(int)QueueType.Error] += (uint)items.Where(i => i.Queue.Type == QueueType.Error).Count();
            changedItemsCount = true;
          }

        }
      }

      // Oldest first
      if( items.Count > 1 )
        items.Sort((a, b) => a.ArrivedTime.CompareTo(b.ArrivedTime));

      bool changed = false;
      lock( _itemsLock ) {

        // If changed Monitor Queues while fetching remove then from result.
        List<QueueType> removedQueueTypes = new List<QueueType>(4);
        foreach( QueueType t in _queueTypeValues ) {
          if( !_monitorState.IsMonitoring(t) && monitorStatesWhenFetch.IsMonitoring(t) ) {
            removedQueueTypes.Add(t);
          }
        }
        if( removedQueueTypes.Any() )
          items = items.Where(i => !removedQueueTypes.Any(x => x == i.Queue.Type)).ToList();


        // Add new items
        foreach( var itm in items ) {
          var existingItem = _items.SingleOrDefault(i => i.Id == itm.Id);

          if( existingItem == null ) {

            _items.Insert(0, new QueueItemViewModel(itm, _mgr.MessagesHasMilliSecondPrecision));

            if( !changed )
              changed = true;

          } else if( existingItem.Processed ) {

            // It has been retried, move to top
            _items.Remove(existingItem);
            existingItem.Processed = false;

            _items.Insert(0, existingItem);

            if( !changed )
              changed = true;
          }

        }

        // Mark removed as deleted messages
        foreach( var itm in _items )
          if( !unchangedQueues.Contains(itm.Queue.Name) )
            if( !items.Any(i2 => i2.Id == itm.Id) ) {

              if( !itm.Processed ) {
                itm.Processed = true;

                if( !changed )
                  changed = true;
              }
            }

      }

      return changed || changedItemsCount;
    }
    public void RetrieveProcessedQueueItems(TimeSpan timeSpan) {
      if( _mgr.MonitorQueues.Length == 0 )
        return;

      List<QueueItem> items = new List<QueueItem>();

      // TODO: Solve why we can not iterate thru Remote MQ, 
      // both GetMessageEnumerator2() and GetAllMessages() should be available for
      // Remote computer and direct format name, but returns zero (0) messages always
      //if( !Tools.IsLocalHost(_serverName) )
      //  return;
      DateTime since = DateTime.Now - timeSpan;

      foreach( QueueType t in Enum.GetValues(typeof(QueueType)) )
        if( _monitorState.MonitorQueueType[(int)t] ) {
          var r = _mgr.GetProcessedMessages(t, since, _items.AsEnumerable<QueueItem>());
          items.AddRange(r.Items);
        }

      bool changed = false;
      lock( _itemsLock ) {

        // Add new items
        foreach( var itm in items )
          if( !_items.Any(i => i.Id == itm.Id) ) {

            _items.Add(new QueueItemViewModel(itm, _mgr.MessagesHasMilliSecondPrecision));

            if( !changed )
              changed = true;
          }

      }

      if( changed ) {
        _items.Sort((a, b) => b.ArrivedTime.CompareTo(a.ArrivedTime));

        OnItemsChanged(ItemChangeOrigin.Queue);
      }
    }

    public void ClearProcessedItems() {

      lock( _itemsLock ) {

        foreach( var itm in _items.Where(i => i.Processed).ToArray() )
          _items.Remove(itm);
      }
    }


    private void System_ErrorOccured(object sender, ErrorArgs e) {
      OnError(e);

      if( e.Fatal )
        Application.Current.Shutdown();
    }
    private void System_WarningOccured(object sender, WarningArgs e) {
      OnWarning(e);
    }
    private void System_ItemsChanged(object sender, EventArgs e) {
      OnItemsChanged(ItemChangeOrigin.Queue);
      //_itemsChanged.Invoke(sender, e);
    }

    protected void OnItemsChanged(ItemChangeOrigin origin) {
      if( _itemsChanged != null )
        _itemsChanged(this, new ItemsChangedEventArgs() { Origin = origin });
    }

    protected EventHandler<ItemsChangedEventArgs> _itemsChanged;
    public event EventHandler<ItemsChangedEventArgs> ItemsChanged {
      [MethodImpl(MethodImplOptions.Synchronized)]
      add {
        _itemsChanged = (EventHandler<ItemsChangedEventArgs>)Delegate.Combine(_itemsChanged, value);
      }
      [MethodImpl(MethodImplOptions.Synchronized)]
      remove {
        _itemsChanged = (EventHandler<ItemsChangedEventArgs>)Delegate.Remove(_itemsChanged, value);
      }
    }


    public Type[] GetAvailableCommands(bool suppressErrors = false) {
      var sc = _mgr as ISendCommand;
      if( sc != null )
        return sc.GetAvailableCommands(Config.CurrentServer.CommandsAssemblyPaths, Config.CurrentServer.CommandDefinition, suppressErrors);
      else return new Type[0];
    }
    public Type[] GetAvailableCommands(string[] _asmPath, CommandDefinition cmdDef, bool suppressErrors = false) {
      var sc = _mgr as ISendCommand;
      if( sc != null )
        return sc.GetAvailableCommands(_asmPath, cmdDef, suppressErrors);
      else return new Type[0];
    }


    public MessageSubscription[] GetMessageSubscriptions(Dictionary<string, object> connectionSettings, IEnumerable<string> queues) {
      var sc = _mgr as IViewSubscriptions;
      if( sc != null )
        return sc.GetMessageSubscriptions(connectionSettings, queues);
      else return new MessageSubscription[0];
    }
    public MessageSubscription[] GetMessageSubscriptions(ServerConfig3 server) {
      IViewSubscriptions sc = null;

      if( server.ServiceBus == _mgr.ServiceBusName &&
              server.ServiceBusVersion == _mgr.ServiceBusVersion &&
              server.ServiceBusQueueType == _mgr.MessageQueueType ) {
        sc = _mgr as IViewSubscriptions;
      } else {
        var mgr = ServiceBusFactory.CreateManager(server.ServiceBus, server.ServiceBusVersion, server.ServiceBusQueueType);
        sc = mgr as IViewSubscriptions;
      }

      if( sc != null )
        return sc.GetMessageSubscriptions(server.ConnectionSettings, server.MonitorQueues.Select(q => q.Name));
      else return new MessageSubscription[0];

    }


    public void SendCommand(Dictionary<string, object> connectionStrings, string destinationQueue, object message) {
      var sc = _mgr as ISendCommand;
      if( sc != null ) {

        if( !_isServiceBusStarted ) {
          sc.SetupServiceBus(Config.CurrentServer.CommandsAssemblyPaths, Config.CurrentServer.CommandDefinition, Config.CurrentServer.ConnectionSettings);
          _isServiceBusStarted = true;
        }

        sc.SendCommand(connectionStrings, destinationQueue, message);

      } else throw new Exception("This Service Bus Adapter does not support sending Commands");
    }



    private Assembly SbmqmDomain_AssemblyResolve(object sender, ResolveEventArgs args) {
      string asmName = args.Name.Split(',')[0];
      bool hasFullAsmName = args.Name.Contains(',');

      // Resolve from Adapters
      var root = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
      var fn = Path.Combine(root, asmName + ".dll");
      if( File.Exists(fn) ) {
        return Assembly.LoadFrom(fn);
      } else {

        try {
          var mgrFilePath = ServiceBusFactory.GetManagerFilePath(Config.ServiceBus, Config.ServiceBusVersion, Config.ServiceBusQueueType);
          if( mgrFilePath.IsValid() ) {
            string adapterPath = Path.GetDirectoryName(mgrFilePath);

            fn = Path.Combine(adapterPath, asmName + ".dll");
            if( File.Exists(fn) && ( !hasFullAsmName || AssemblyName.GetAssemblyName(fn).FullName == args.Name ) )
              return Assembly.LoadFrom(fn);

          }
        } catch( NoMessageBusManagerFound ) {
        }

        string adaptersPath = root + "\\Adapters\\";
        foreach( var dir in Directory.GetDirectories(adaptersPath) ) {
          fn = Path.Combine(dir, asmName + ".dll");
          if( File.Exists(fn) && ( !hasFullAsmName || AssemblyName.GetAssemblyName(fn).FullName == args.Name ) )
            return Assembly.LoadFrom(fn);
        }

      }


      // Resolve from Assembly Paths
      if( Config != null ) {
        foreach( var path in Config.CurrentServer.CommandsAssemblyPaths ) {
          var fileName = string.Format("{0}\\{1}.dll", path, asmName);

          try {

            if( File.Exists(fileName) ) { // && AssemblyName.GetAssemblyName(fileName).FullName == args.Name ) {
              return Assembly.LoadFrom(fileName);
            }

          } catch { }
        }
      }



      if( !args.Name.StartsWith("mscorlib.XmlSerializers") )
        throw new ApplicationException("Failed resolving assembly, " + args.Name);

      return null;
    }

    static string _appDataPath = null;
    public static string AppDataPath {
      get {
        if( _appDataPath == null ) {
          _appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\SBMQM\";

          if( !Directory.Exists(_appDataPath) )
            Directory.CreateDirectory(_appDataPath);
        }

        return _appDataPath;
      }
    }


    public bool MonitorCommands {
      get { return (bool)_monitorState.MonitorQueueType[(int)QueueType.Command]; }
      set { MonitorStateChanged(QueueType.Command, value); }
    }
    public bool MonitorEvents {
      get { return (bool)_monitorState.MonitorQueueType[(int)QueueType.Event]; }
      set { MonitorStateChanged(QueueType.Event, value); }
    }
    public bool MonitorMessages {
      get { return (bool)_monitorState.MonitorQueueType[(int)QueueType.Message]; }
      set { MonitorStateChanged(QueueType.Message, value); }
    }
    public bool MonitorErrors {
      get { return (bool)_monitorState.MonitorQueueType[(int)QueueType.Error]; ; }
      set { MonitorStateChanged(QueueType.Error, value); }
    }


    private void MonitorStateChanged(QueueType type, bool value) {
      _monitorState.MonitorQueueType[(int)type] = value;

      if( !value ) {
        lock( _itemsLock ) {
          foreach( var itm in _items.Where(i => i.Queue.Type == type).ToArray() )
            _items.Remove(itm);
        }
      }
    }

    public event EventHandler<ErrorArgs> ErrorOccured;
    public event EventHandler<WarningArgs> WarningOccured;

    protected void OnError(string message, Exception exception = null, bool fatal = false) {
      if( ErrorOccured != null )
        ErrorOccured(this, new ErrorArgs(message, exception, fatal));
    }
    protected void OnError(ErrorArgs arg) {
      if( ErrorOccured != null )
        ErrorOccured(this, arg);
    }

    protected void OnWarning(WarningArgs arg) {
      if( WarningOccured != null )
        WarningOccured(this, arg);
    }

    protected EventHandler<EventArgs> _startedLoadingQueues;
    public event EventHandler<EventArgs> StartedLoadingQueues {
      [MethodImpl(MethodImplOptions.Synchronized)]
      add {
        _startedLoadingQueues = (EventHandler<EventArgs>)Delegate.Combine(_startedLoadingQueues, value);
      }
      [MethodImpl(MethodImplOptions.Synchronized)]
      remove {
        _startedLoadingQueues = (EventHandler<EventArgs>)Delegate.Remove(_startedLoadingQueues, value);
      }

    }
    protected void OnStartedLoadingQueues() {
      if( _startedLoadingQueues != null )
        _startedLoadingQueues(this, EventArgs.Empty);
    }

    protected EventHandler<EventArgs> _finishedLoadingQueues;
    public event EventHandler<EventArgs> FinishedLoadingQueues {
      [MethodImpl(MethodImplOptions.Synchronized)]
      add {
        _finishedLoadingQueues = (EventHandler<EventArgs>)Delegate.Combine(_finishedLoadingQueues, value);
      }
      [MethodImpl(MethodImplOptions.Synchronized)]
      remove {
        _finishedLoadingQueues = (EventHandler<EventArgs>)Delegate.Remove(_finishedLoadingQueues, value);
      }
    }

    protected void OnFinishedLoadingQueues() {
      if( _finishedLoadingQueues != null )
        _finishedLoadingQueues(this, EventArgs.Empty);
    }



    public void FilterItems(string str) {

      if( str.IsValid() ) {
        _filter = str;
        OnItemsChanged(ItemChangeOrigin.Filter);

      } else ClearFilter();

    }

    public void ClearFilter() {
      _filter = null;
      OnItemsChanged(ItemChangeOrigin.Filter);
    }


    private void InvokeWhileMonitoringPaused(Action a) {
      if( _currentMonitor == null )
        return;

      BackgroundWorker bw = new BackgroundWorker();

      bw.DoWork += (sender, arg) => {
        ThreadState s = arg.Argument as ThreadState;
        PauseMonitoring();
        OnStartedLoadingQueues();

        while( !s.Paused )
          Thread.Sleep(100);

        try {
          a();

        } finally {
          OnFinishedLoadingQueues();
          ResumeMonitoring();
        }

      };

      bw.RunWorkerCompleted += (object s, RunWorkerCompletedEventArgs ev) => {
        if( ev.Error != null )
          throw ev.Error;
      };

      bw.RunWorkerAsync(_currentMonitor);
    }

    public async Task PurgeAllMessages() {
      if( _currentMonitor == null )
        return;

      BackgroundWorker bw = new BackgroundWorker();

      bw.DoWork += (sender, arg) => {
        ThreadState s = arg.Argument as ThreadState;
        PauseMonitoring();
        OnStartedLoadingQueues();

        while( !s.Paused )
          Thread.Sleep(100);

        try {
          _mgr.PurgeAllMessages();

        } finally {
          OnFinishedLoadingQueues();
          ResumeMonitoring();
        }

      };

      bw.RunWorkerCompleted += (object s, RunWorkerCompletedEventArgs ev) => {
        if( ev.Error != null )
          throw ev.Error;
      };

      bw.RunWorkerAsync(_currentMonitor);
    }
    public async Task PurgeMessages(IEnumerable<QueueItem> itms) {
      if( _currentMonitor == null )
        return;

      BackgroundWorker bw = new BackgroundWorker();

      bw.DoWork += (sender, arg) => {
        ThreadState s = arg.Argument as ThreadState;
        PauseMonitoring();
        OnStartedLoadingQueues();

        while( !s.Paused )
          Thread.Sleep(100);

        try {
          foreach( var itm in itms )
            _mgr.PurgeMessage(itm);

        } finally {
          OnFinishedLoadingQueues();
          ResumeMonitoring();
        }

      };

      bw.RunWorkerCompleted += (object s, RunWorkerCompletedEventArgs ev) => {
        if( ev.Error != null )
          throw ev.Error;
      };

      bw.RunWorkerAsync(_currentMonitor);
    }

    public async Task PurgeErrorAllMessages() {
      if( _currentMonitor == null )
        return;

      BackgroundWorker bw = new BackgroundWorker();

      bw.DoWork += (sender, arg) => {
        ThreadState s = arg.Argument as ThreadState;
        StopMonitoring();
        OnStartedLoadingQueues();

        while( !s.Stopped )
          Thread.Sleep(100);

        try {
          _mgr.PurgeErrorAllMessages();

        } finally {
          OnFinishedLoadingQueues();
          StartMonitoring();
        }

      };

      bw.RunWorkerCompleted += (object s, RunWorkerCompletedEventArgs ev) => {
        if( ev.Error != null )
          throw ev.Error;
      };

      bw.RunWorkerAsync(_currentMonitor);

    }

    public void PurgeMessage(QueueItem itm) {

      InvokeWhileMonitoringPaused(() => {
        _mgr.PurgeMessage(itm);
      });

    }

    public async Task PurgeErrorMessages(string queueName) {
      InvokeWhileMonitoringPaused(() => {
        _mgr.PurgeErrorMessages(queueName);
      });
    }

    public async Task MoveAllErrorMessagesToOriginQueue(string errorQueue) {
      _log.Trace("MoveAllErrorMessagesToOriginQueue: " + errorQueue);

      InvokeWhileMonitoringPaused(() => {
        _mgr.MoveAllErrorMessagesToOriginQueue(errorQueue);
      });

    }

    public async Task MoveErrorMessageToOriginQueue(QueueItem itm) {
      InvokeWhileMonitoringPaused(() => {
        _mgr.MoveErrorMessageToOriginQueue(itm);
      });
    }

  }

}
