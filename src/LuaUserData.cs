
using System;

namespace NLua {
    public class LuaUserData : LuaBase {
        public LuaUserData(int reference, Lua interpreter) : base(reference, interpreter) {
        }

        public object this[string field] {
            get => Lua.GetObjectField(Reference, field);
            set => Lua.SetObjectField(Reference, field, value);
        }

        public object this[object field] {
            get => Lua.GetObjectField(Reference, field);
            set => Lua.SetObjectField(Reference, field, value);
        }

        public object[] Call(params object[] args)
            => Lua.CallFunction(this, args);
    }
}
