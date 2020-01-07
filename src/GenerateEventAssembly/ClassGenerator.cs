using System;
using LuaState = KeraLua.Lua;

namespace NLua {
    class ClassGenerator {
        private readonly ObjectTranslator Translator;
        private readonly Type Type;

        public ClassGenerator(ObjectTranslator translator, Type type) {
            Translator = translator;
            Type = type;
        }

        public object ExtractGenerated(LuaState luaState, int stackPos) {
            return CodeGeneration.GetClassInstance(Type, Translator.GetTable(stackPos));
        }
    }
}
