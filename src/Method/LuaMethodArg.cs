using System;

namespace NLua.Method {
    /// <summary>
	/// Parameter information used by MethodCache with additional information and a value extractor.
    /// </summary>
    class LuaMethodArg {
        public int Index;
        public Type ParameterType;

        public ExtractValue ExtractValue;
        public bool IsParamsArray;
    }
}
