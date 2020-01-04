using System;
using KeraLua;

using LuaState = KeraLua.Lua;
using LuaNativeFunction = KeraLua.LuaFunction;

namespace NLua
{
    public class LuaThread : LuaBase
    {

        public LuaThread(int reference, Lua interpreter):base(reference, interpreter)
        {
        }

        /*
         * Pushes the thread into the Lua stack
         */
        internal void Push(LuaState luaState)
        {
            Lua lua;
            if (!TryGet(out lua))
                return;

            luaState.RawGetInteger(LuaRegistry.Index, _Reference);
        }

        public override string ToString()
        {
            return "function";
        }

        public override bool Equals(object o)
        {
            var l = o as LuaThread;

            if (l == null)
                return false;

            Lua lua;
            if (!TryGet(out lua))
                return false;

            return lua.CompareRef(l._Reference, _Reference);
        }

        public override int GetHashCode()
        {
            return _Reference;
        }
    }
}