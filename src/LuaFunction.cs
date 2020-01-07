using System;
using KeraLua;

using LuaState = KeraLua.Lua;
using LuaNativeFunction = KeraLua.LuaFunction;
using NLua.Extensions;

namespace NLua {
    public class LuaFunction : LuaBase {
        internal readonly LuaNativeFunction _Function;

        public LuaFunction(int reference, Lua lua)
            : base(reference, lua) {
        }

        public LuaFunction(LuaNativeFunction function, Lua lua)
            : base(0, lua) {
            _Function = function;
        }

        /// <summary>
        /// Calls the function casting return values to the types in returnTypes
        /// </summary>
        internal object[] Call(object[] args, Type[] returnTypes)
            => Lua.CallFunction(this, args, returnTypes);

        /// <summary>
        /// Calls the function and returns its return values inside an array
        /// </summary>
        public object[] Call(params object[] args)
            => Lua.CallFunction(this, args);

        protected override void Push() {
            if (Reference != 0)
                Lua.State.GetRef(Reference);
            else
                Lua.Translator.Push(_Function);
        }

        public override bool Equals(object o) {
            if (!(o is LuaFunction l))
                return false;
            if (Reference != 0 && l.Reference != 0)
                return Lua.CompareRef(l.Reference, Reference);
            return _Function == l._Function;
        }

        public override int GetHashCode() {
            return Reference != 0 ? Reference : _Function.GetHashCode();
        }
    }
}
