using System;
using System.Reflection;

namespace NLua {
    /// <summary>
    /// Used to allow accessing static members of a given type typeof(T) without conflicting with typeof(Type)'s members.
    /// </summary>
    public class LuaStaticMemberProxy {
        public readonly Type Type;

        public LuaStaticMemberProxy(Type type) {
            Type = type;
        }

        public override string ToString() {
            return "LuaStaticAccessProxy(" + Type + ")";
        }

        public override bool Equals(object obj) {
            if (obj is Type)
                return Type == (Type) obj;
            if (obj is LuaStaticMemberProxy)
                return Type == ((LuaStaticMemberProxy) obj).Type;
            return Type.Equals(obj);
        }

        public override int GetHashCode() {
            return Type.GetHashCode();
        }
    }
}
