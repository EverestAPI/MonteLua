using System;
using System.Reflection;

namespace NLua.Method {
    class RegisterEventHandler {
        private readonly EventHandlerContainer _pendingEvents;
        private readonly EventInfo _eventInfo;
        private readonly object _target;

        public RegisterEventHandler(EventHandlerContainer pendingEvents, object target, EventInfo eventInfo) {
            _target = target;
            _eventInfo = eventInfo;
            _pendingEvents = pendingEvents;
        }

        public Delegate Add(LuaFunction function) {
            return Add(CodeGeneration.GetDelegate(_eventInfo.EventHandlerType, function));
        }

        public Delegate Add(Delegate handlerDelegate) {
            _eventInfo.AddEventHandler(_target, handlerDelegate);
            _pendingEvents.Add(handlerDelegate, this);
            return handlerDelegate;
        }

        public void Remove(Delegate handlerDelegate) {
            RemovePending(handlerDelegate);
            _pendingEvents.Remove(handlerDelegate);
        }

        /// <summary>
        /// Removes an existing event handler (without updating the pending handlers list)
        /// </summary>
        internal void RemovePending(Delegate handlerDelegate) {
            _eventInfo.RemoveEventHandler(_target, handlerDelegate);
        }
    }
}
