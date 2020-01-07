using System;

namespace NLua.Method {
    public class LuaDelegate {
        public LuaFunction Function;
        public Type[] ReturnTypes;

        public LuaDelegate() {
            Function = null;
            ReturnTypes = null;
        }

        public object CallFunction(object[] args, object[] inArgs, int[] outArgs) {
            return LuaClassHelper.CallFunction(Function, args, ReturnTypes, inArgs, outArgs);
        }
    }
}
