using System;
using System.Collections.Concurrent;

using LuaState = KeraLua.Lua;

namespace NLua {
    internal static class LuaPool {
        private static readonly ConcurrentDictionary<LuaState, Lua> Map = new ConcurrentDictionary<LuaState, Lua>();

        public static void Add(LuaState state, Lua lua)
            => Map[state] = lua;

        public static Lua Find(LuaState state)
            => Map.TryGetValue(state, out Lua lua) || Map.TryGetValue(state.MainThread, out lua) ? lua : null;

        public static void Remove(LuaState state)
            => Map.TryRemove(state, out Lua lua);
    }
}
