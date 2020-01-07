using System;
using LuaState = KeraLua.Lua;

namespace NLua {
    class DelegateGenerator {
        private readonly ObjectTranslator Translator;
        private readonly Type Type;

        public DelegateGenerator(ObjectTranslator objectTranslator, Type type) {
            Translator = objectTranslator;
            Type = type;
        }

        public object ExtractGenerated(LuaState luaState, int stackPos) {
            return CodeGeneration.GetDelegate(Type, Translator.GetFunction(stackPos));
        }
    }
}
