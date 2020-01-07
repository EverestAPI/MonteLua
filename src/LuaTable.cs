
using System;
using System.Collections;
using System.Collections.Generic;
using NLua.Extensions;

using LuaState = KeraLua.Lua;

namespace NLua {
    public class LuaTable : LuaBase {
        public LuaTable(int reference, Lua lua)
            : base(reference, lua) {
        }

        public object this[string field] {
            get => Lua.GetObjectField(Reference, field);
            set => Lua.SetObjectField(Reference, field, value);
        }

        public object this[object field] {
            get => Lua.GetObjectField(Reference, field);
            set => Lua.SetObjectField(Reference, field, value);
        }

        public IDictionaryEnumerator GetEnumerator()
            => ToDictionary().GetEnumerator();

        public ICollection Keys => ToDictionary().Keys;
        public ICollection Values => ToDictionary().Values;

        public Dictionary<object, object> ToDictionary() {
            Dictionary<object, object> dict = new Dictionary<object, object>();

            using (SafeLuaContext ctx = new SafeLuaContext(Lua)) {
                Push();
                Lua.State.PushNil();

                while (Lua.State.Next(-2)) {
                    dict[Lua.Translator.GetObject(-2)] = Lua.Translator.GetObject(-1);
                    Lua.State.SetTop(-2);
                }

                return dict;
            }
        }
    }
}
