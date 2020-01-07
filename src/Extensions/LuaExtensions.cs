
using System;
using System.IO;
using System.Runtime.InteropServices;
using KeraLua;
using LuaState = KeraLua.Lua;

namespace NLua.Extensions {
    static class LuaExtensions {
        /// <summary>
        /// Execute a .lua file embedded in the MonteLua assembly.
        /// </summary>
        public static bool DoEmbeddedFile(this LuaState state, string name) {
            string code;
            using (StreamReader reader = new StreamReader(typeof(Lua).Assembly.GetManifestResourceStream($"NLua.src.{name}.lua")))
                code = reader.ReadToEnd();
            return state.DoString(code);
        }

        public static bool CheckMetaTable(this LuaState state, int index, IntPtr tag) {
            if (!state.GetMetaTable(index))
                return false;

            state.PushLightUserData(tag);
            state.RawGet(-2);
            bool isNotNil = !state.IsNil(-1);
            state.SetTop(-3);
            return isNotNil;
        }

        /// <summary>
        /// KeraLua has got PushGlobalTable but lacks PopGlobalTable.
        /// </summary>
        public static void PopGlobalTable(this LuaState luaState) {
            luaState.RawSetInteger(LuaRegistry.Index, (long) LuaRegistryIndex.Globals);
        }

        /// <summary>
        /// Stores a statically held reference.
        /// </summary>
        public static void GetRef(this LuaState luaState, int reference) {
            luaState.RawGetInteger(LuaRegistry.Index, reference);
        }

        /// <summary>
        /// Frees a statically held reference.
        /// </summary>
        public static void Unref(this LuaState luaState, int reference) {
            luaState.Unref(LuaRegistry.Index, reference);
        }

        /// <summary>
        /// Check if the given userdata matches the given registered userdata type.
        /// </summary>
        public static bool IsUserdataType(this LuaState state, int ud, string name, out IntPtr udPtr) {
            udPtr = state.ToUserData(ud);
            if (udPtr == IntPtr.Zero)
                return false;
            if (!state.GetMetaTable(ud))
                return false;

            state.GetField(LuaRegistry.Index, name);

            bool isEqual = state.RawEqual(-1, -2);

            state.Pop(2);

            if (isEqual)
                return true;

            return false;
        }

        /// <summary>
        /// Check if the given userdata matches the given registered userdata type.
        /// </summary>
        public static int AsUserdataType(this LuaState state, int index, string name) {
            if (!state.IsUserdataType(index, name, out IntPtr ud))
                return -1;
            return Marshal.ReadInt32(ud);
        }

        /// <summary>
        /// Gets the CLR object in the index positon of the Lua stack.
        /// Returns delegates as Lua functions.
        /// </summary>
        public static int ToNetObject(this LuaState state, int index, IntPtr tag) {
            if (state.Type(index) != LuaType.UserData)
                return -1;

            IntPtr userData;

            if (state.CheckMetaTable(index, tag)) {
                userData = state.ToUserData(index);
                if (userData != IntPtr.Zero)
                    return Marshal.ReadInt32(userData);
            }

            if (state.IsUserdataType(index, "luaNet_class", out userData) ||
                state.IsUserdataType(index, "luaNet_searchbase", out userData) ||
                state.IsUserdataType(index, "luaNet_function", out userData))
                return Marshal.ReadInt32(userData);

            return -1;
        }

        /// <summary>
        /// Gets the CLR object in the index positon of the Lua stack.
        /// Returns delegates as is.
        /// </summary>
        public static int ToRawNetObject(this LuaState state, int index) {
            IntPtr pointer = state.ToUserData(index);
            if (pointer == IntPtr.Zero)
                return -1;
            return Marshal.ReadInt32(pointer);
        }

        /// <summary>
        /// Same as NewUserData + writing the value into the UserData pointer.
        /// </summary>
        public static void NewUserDataWithValue(this LuaState state, int val) {
            IntPtr ud = state.NewUserData(Marshal.SizeOf(typeof(int)));
            Marshal.WriteInt32(ud, val);
        }

        /// <summary>
        /// Shorthand for .Type(index) == LuaType.Number
        /// </summary>
        public static bool IsNumericType(this LuaState state, int index) {
            return state.Type(index) == LuaType.Number;
        }
    }
}
