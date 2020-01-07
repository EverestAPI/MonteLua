using System;

namespace NLua {
    /// <summary>
    /// Structure to store a type and the return types of
    /// its methods (the type of the returned value and out/ref
    /// parameters).
    /// </summary>
    struct LuaClassType {
        public Type Type;
        public Type[][] ReturnTypes;
    }
}
