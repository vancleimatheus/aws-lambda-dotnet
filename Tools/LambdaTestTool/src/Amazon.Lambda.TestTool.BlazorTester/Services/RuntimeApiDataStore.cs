using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace Amazon.Lambda.TestTool.BlazorTester.Services
{
    public interface IRuntimeApiDataStore
    {
        EventContainer QueueEvent(string eventBody);
        
        IReadOnlyList<IEventContainer> QueuedEvents { get; }
        
        IReadOnlyList<IEventContainer> ExecutedEvents { get; }
        
        IEventContainer ActiveEvent { get; }

        event EventHandler StateChange;

        bool TryActivateEvent(out IEventContainer activeEvent);

        void ReportSuccess(string awsRequestId, string response);
        void ReportError(string awsRequestId, string errorType, string errorBody);
    }

    public class RuntimeApiDataStore : IRuntimeApiDataStore
    {
        private Queue<EventContainer> _queuedEvents = new Queue<EventContainer>();
        private IList<EventContainer> _executedEvents = new List<EventContainer>();
        private int _eventCounter = 1;
        private object _lock = new object();
        
        public event EventHandler StateChange;
        
        public EventContainer QueueEvent(string eventBody)
        {
            var evnt = new EventContainer(this, _eventCounter++, eventBody);
            Monitor.Enter(_lock);
            try
            {
                _queuedEvents.Enqueue(evnt);
                Monitor.PulseAll(_lock);
            }
            finally
            {
                Monitor.Exit(_lock);
            }
            
            RaiseStateChanged();
            return evnt;
        }

        public bool TryActivateEvent(out IEventContainer activeEvent)
        {
            activeEvent = null;
            Monitor.Enter(_lock);
            try
            {
                if (!_queuedEvents.TryDequeue(out var evnt))
                {
                    return false;
                }

                evnt.EventStatus = IEventContainer.Status.Executing;
                if (ActiveEvent != null)
                {
                    _executedEvents.Add(ActiveEvent as EventContainer);
                }
                ActiveEvent = evnt;
                activeEvent = ActiveEvent;
                RaiseStateChanged();
                Monitor.PulseAll(_lock);
                return true;
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }
        
        public IEventContainer ActiveEvent { get; private set; }

        public IReadOnlyList<IEventContainer> QueuedEvents
        {
            get
            {
                Monitor.Enter(_lock);
                try
                {
                    return new ReadOnlyCollection<EventContainer>(_queuedEvents.ToList()); 
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
        }

        public IReadOnlyList<IEventContainer> ExecutedEvents
        {
            get
            {
                Monitor.Enter(_lock);
                try
                {
                    return new ReadOnlyCollection<EventContainer>(_executedEvents);
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
        }

        public void ReportSuccess(string awsRequestId, string response)
        {
            Monitor.Enter(_lock);
            try
            {
                var evnt = FindEventContainer(awsRequestId);
                if (evnt == null)
                {
                    return;
                }
                
                evnt.ReportSuccessResponse(response);
                RaiseStateChanged();
                Monitor.PulseAll(_lock);
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }
        
        public void ReportError(string awsRequestId, string errorType, string errorBody)
        {
            Monitor.Enter(_lock);
            try
            {
                var evnt = FindEventContainer(awsRequestId);
                if (evnt == null)
                {
                    return;
                }
                
                evnt.ReportErrorResponse(errorType, errorBody);
                RaiseStateChanged();
                Monitor.PulseAll(_lock);                
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        private EventContainer FindEventContainer(string awsRequestId)
        {
            if (string.Equals(this.ActiveEvent?.AwsRequestId, awsRequestId))
            {
                return this.ActiveEvent as EventContainer;
            }

            var evnt = _executedEvents.FirstOrDefault(x => string.Equals(x.AwsRequestId, awsRequestId)) as EventContainer;
            if (evnt != null)
            {
                return evnt;
            }

            evnt = _queuedEvents.FirstOrDefault(x => string.Equals(x.AwsRequestId, awsRequestId)) as EventContainer;

            return evnt;
        }

        internal void RaiseStateChanged()
        {
            var handler = StateChange;
            handler?.Invoke(this, EventArgs.Empty);
        }
    }

    public interface IEventContainer
    {
        public enum Status {Queued, Executing, Success, Failure}
        
        string AwsRequestId { get; }
        string EventJson { get; }
        string ErrorResponse { get; }
        string ErrorType { get; }
        
        string Response { get; }
        Status EventStatus { get; }
        
        string FunctionArn { get; }
        
        DateTime LastUpdated { get; }
    }

    public class EventContainer : IEventContainer
    {
        
        private const string defaultFunctionArn = "arn:aws:lambda:us-west-2:123412341234:function:Function";
        public string AwsRequestId { get; }
        public string EventJson { get; }
        public string ErrorResponse { get; private set; }
        
        public string ErrorType { get; private set; }
        
        public string Response { get; private set; }
        
        public DateTime LastUpdated { get; private set; }

        private IEventContainer.Status _status = IEventContainer.Status.Queued;
        public IEventContainer.Status EventStatus
        {
            get => _status;
            set
            {
                _status = value;
                LastUpdated = DateTime.Now;
            }
        }

        private readonly RuntimeApiDataStore _dataStore;
        public EventContainer(RuntimeApiDataStore dataStore, int eventCount, string eventJson)
        {
            LastUpdated = DateTime.Now;
            this._dataStore = dataStore;
            this.AwsRequestId = eventCount.ToString("D12");
            this.EventJson = eventJson;
        }

        public string FunctionArn
        {
            get => defaultFunctionArn;
        }

        public void ReportSuccessResponse(string response)
        {
            LastUpdated = DateTime.Now;
            this.Response = response;
            this.EventStatus = IEventContainer.Status.Success;
            _dataStore.RaiseStateChanged();
        }
        
        public void ReportErrorResponse(string errorType, string errorBody)
        {
            LastUpdated = DateTime.Now;
            this.ErrorType = errorType;
            this.ErrorResponse = errorBody;
            this.EventStatus = IEventContainer.Status.Failure;
            _dataStore.RaiseStateChanged();
        }
    }
}