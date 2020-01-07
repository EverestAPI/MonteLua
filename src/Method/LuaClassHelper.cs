using System;

namespace NLua.Method {
    /// <summary>
    /// Helpers for automatically generated "Lua classes".
    /// </summary>
    public class LuaClassHelper {
        /// <summary>
        /// Gets the function called name from the provided table,
        /// returning null if it does not exist
        /// </summary>
        public static LuaFunction GetTableFunction(LuaTable luaTable, string name) {
            if (luaTable == null)
                return null;
            return luaTable[name] as LuaFunction;
        }

        /// <summary>
        /// Calls the provided function with the provided parameters
        /// </summary>
        public static object CallFunction(LuaFunction function, object[] args, Type[] returnTypes, object[] inArgs, int[] outArgs) {
            // args is the return array of arguments, inArgs is the actual array
            // of arguments passed to the function (with in parameters only), outArgs
            // has the positions of out parameters
            object returnValue;
            int iRefArgs;
            object[] returnValues = function.Call(inArgs, returnTypes);

            if (returnTypes[0] == typeof(void)) {
                returnValue = null;
                iRefArgs = 0;
            } else {
                returnValue = returnValues[0];
                iRefArgs = 1;
            }

            for (int i = 0; i < outArgs.Length; i++) {
                args[outArgs[i]] = returnValues[iRefArgs];
                iRefArgs++;
            }

            return returnValue;
        }
    }
}
