using System;
using KeraLua;

using LuaState = KeraLua.Lua;
using LuaNativeFunction = KeraLua.LuaFunction;

namespace NLua {
    public class LuaThread : LuaBase {
        public LuaThread(int reference, Lua lua)
            : base(reference, lua) {
        }
    }
}
