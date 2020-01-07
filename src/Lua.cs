using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KeraLua;
using System.IO;

using NLua.Method;
using NLua.Exceptions;
using NLua.Extensions;

using LuaState = KeraLua.Lua;
using LuaNativeFunction = KeraLua.LuaFunction;
using System.Text;

namespace NLua {
    public class Lua : IDisposable {
        public readonly LuaState State;

        public readonly ObjectTranslator Translator;

        public bool IsExecuting { get; internal set; }

        public bool UseTraceback { get; set; } = false;

        public Lua() {
            State = new LuaState();
            State.AtPanic(PanicCallback);
            LuaPool.Add(State, this);

            State.NewTable();
            State.SetGlobal("luanet");
            State.PushGlobalTable();
            State.GetGlobal("luanet");
            State.PushString("getmetatable");
            State.GetGlobal("getmetatable");
            State.SetTable(-3);
            State.PopGlobalTable();
            Translator = new ObjectTranslator(this);

            State.PopGlobalTable();

            State.DoEmbeddedFile("init");
        }

        public void Close() {
            State.Close();
            LuaPool.Remove(State);
        }

        private static int PanicCallback(IntPtr statePtr) {
            LuaState state = LuaState.FromIntPtr(statePtr);
            throw new LuaException(string.Format("Unprotected error in call to Lua API ({0})", state.ToString(-1, false)));
        }

        /// <summary>
        /// Assuming we have a Lua error string sitting on the stack, throw a C# exception out to the user's app
        /// </summary>
        /// <exception cref = "LuaScriptException">Thrown if the script caused an exception</exception>
        internal void ThrowExceptionFromError(int oldTop) {
            object err = Translator.GetObject(-1);
            State.SetTop(oldTop);

            // A pre-wrapped exception - just rethrow it (stack trace of InnerException will be preserved)
            if (err is LuaScriptException luaEx)
                throw luaEx;

            // A non-wrapped Lua error (best interpreted as a string) - wrap it and throw it
            if (err == null)
                err = "Unknown Lua Error";

            throw new LuaScriptException(err.ToString(), string.Empty);
        }

        /// <summary>
        /// Push a debug.traceback reference onto the stack, for a pcall function to use as error handler. (Remember to increment any top-of-stack markers!)
        /// </summary>
        private static int PushDebugTraceback(LuaState luaState, int argCount) {
            luaState.GetGlobal("debug");
            luaState.GetField(-1, "traceback");
            luaState.Remove(-2);
            int errIndex = -argCount - 2;
            luaState.Insert(errIndex);
            return errIndex;
        }

        /// <summary>
        /// <para>Return a debug.traceback() call result (a multi-line string, containing a full stack trace, including C calls.</para>
        /// <para>Note: it won't return anything unless the interpreter is in the middle of execution - that is, it only makes sense to call it from a method called from Lua, or during a coroutine yield.</para>
        /// </summary>
        public string GetDebugTraceback() {
            int oldTop = State.GetTop();
            State.GetGlobal("debug"); // stack: debug
            State.GetField(-1, "traceback"); // stack: debug,traceback
            State.Remove(-2); // stack: traceback
            State.PCall(0, -1, 0);
            return Translator.PopValues(oldTop)[0] as string;
        }

        /// <summary>
        /// Convert C# exceptions into Lua errors
        /// </summary>
        /// <returns>num of things on stack</returns>
        /// <param name="e">null for no pending exception</param>
        internal int SetPendingException(Exception e) {
            if (e == null)
                return 0;
            return Translator.ThrowError(1, e);
        }

        public LuaFunction LoadString(string chunk, string name) {
            using (SafeLuaContext ctx = new SafeLuaContext(this)) {
                ctx.Verify(State.LoadString(chunk, name));
                return Translator.GetFunction(-1);
            }
        }

        public LuaFunction LoadString(byte[] chunk, string name) {
            using (SafeLuaContext ctx = new SafeLuaContext(this)) {
                ctx.Verify(State.LoadBuffer(chunk, name));
                return Translator.GetFunction(-1);
            }
        }

        /// <summary>
        /// Load a File on, and return a LuaFunction to execute the file loaded (useful to see if the syntax of a file is ok)
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public LuaFunction LoadFile(string fileName) {
            using (SafeLuaContext ctx = new SafeLuaContext(this)) {
                ctx.Verify(State.LoadFile(fileName));
                return Translator.GetFunction(-1);
            }
        }

        /// <summary>
        /// Executes a Lua chunk and returns all the chunk's return values in an array.
        /// </summary>
        /// <param name="chunk">Chunk to execute</param>
        /// <param name="chunkName">Name to associate with the chunk. Defaults to "chunk".</param>
        /// <returns></returns>
        public object[] DoString(byte[] chunk, string chunkName = "chunk") {
            using (SafeLuaContext ctx = new SafeLuaContext(this)) {
                ctx.Verify(State.LoadBuffer(chunk, chunkName));

                int errorFunctionIndex = 0;

                if (UseTraceback) {
                    errorFunctionIndex = PushDebugTraceback(State, 0);
                    ctx.Push();
                }

                ctx.Verify(State.PCall(0, -1, errorFunctionIndex));
                return Translator.PopValues(ctx.OldTop);
            }
        }

        /// <summary>
        /// Executes a Lua chunk and returns all the chunk's return values in an array.
        /// </summary>
        /// <param name="chunk">Chunk to execute</param>
        /// <param name="chunkName">Name to associate with the chunk. Defaults to "chunk".</param>
        /// <returns></returns>
        public object[] DoString(string chunk, string chunkName = "chunk") {
            using (SafeLuaContext ctx = new SafeLuaContext(this)) {
                ctx.Verify(State.LoadString(chunk, chunkName));

                int errorFunctionIndex = 0;

                if (UseTraceback) {
                    errorFunctionIndex = PushDebugTraceback(State, 0);
                    ctx.Push();
                }

                ctx.Verify(State.PCall(0, -1, errorFunctionIndex));
                return Translator.PopValues(ctx.OldTop);
            }
        }

        /// <summary>
        /// Executes a Lua file and returns all the chunk's return values in an array.
        /// </summary>
        /// <param name="fileName">Name of the file to execute</param>
        /// <returns></returns>
        public object[] DoFile(string fileName) {
            using (SafeLuaContext ctx = new SafeLuaContext(this)) {
                ctx.Verify(State.LoadFile(fileName));

                int errorFunctionIndex = 0;

                if (UseTraceback) {
                    errorFunctionIndex = PushDebugTraceback(State, 0);
                    ctx.Push();
                }

                ctx.Verify(State.PCall(0, -1, errorFunctionIndex));
                return Translator.PopValues(ctx.OldTop);
            }
        }

        /// <summary>
        /// Navigates a table in the top of the stack, returning the value of the specified field
        /// </summary>
        internal object WalkTableOnStack(string[] remainingPath) {
            object returnValue = null;

            for (int i = 0; i < remainingPath.Length; i++) {
                State.PushString(remainingPath[i]);
                State.GetTable(-2);
                returnValue = Translator.GetObject(-1);

                if (returnValue == null)
                    break;
            }

            return returnValue;
        }

         /// <summary>
         /// Calls the object as a function with the provided arguments and
         /// casting returned values to the types in returnTypes before returning
         /// them in an array
         /// </summary>
        internal object[] CallFunction(object function, object[] args, Type[] returnTypes = null) {
            using (SafeLuaContext ctx = new SafeLuaContext(this)) {
                if (!State.CheckStack(args.Length + 6))
                    throw new LuaException("Lua stack overflow");

                Translator.Push(function);

                for (int i = 0; i < args.Length; i++)
                    Translator.Push(args[i]);

                int errorFunctionIndex = 0;
                if (UseTraceback) {
                    errorFunctionIndex = PushDebugTraceback(State, args.Length);
                    ctx.Push();
                }

                ctx.Verify(State.PCall(args.Length, -1, errorFunctionIndex));

                if (returnTypes != null)
                    return Translator.PopValues(ctx.OldTop, returnTypes);
                return Translator.PopValues(ctx.OldTop);
            }
        }

        internal static string[] FullPathToArray(string fullPath) {
            return fullPath.SplitWithEscape('.', '\\').ToArray();
        }

        internal void DisposeInternal(int reference, bool finalized) {
            if (finalized)
                Translator.AddFinalizedReference(reference);
            else
                State.Unref(reference);
        }

        /// <summary>
        /// Gets a field of the table or userdata corresponding to the provided reference
        /// </summary>
        internal object GetObjectField(int reference, string field) {
            int oldTop = State.GetTop();
            State.GetRef(reference);
            object value = WalkTableOnStack(FullPathToArray(field));
            State.SetTop(oldTop);
            return value;
        }

        /// <summary>
        /// Gets a numeric field of the table or userdata corresponding to the provided reference
        /// </summary>
        internal object GetObjectField(int reference, object field) {
            int oldTop = State.GetTop();
            State.GetRef(reference);
            Translator.Push(field);
            State.GetTable(-2);
            object value = Translator.GetObject(-1);
            State.SetTop(oldTop);
            return value;
        }

        /// <summary>
        /// Sets a field of the table or userdata corresponding the the provided reference to the provided value
        /// </summary>
        internal void SetObjectField(int reference, string field, object val) {
            int oldTop = State.GetTop();
            State.GetRef(reference);

            string[] path = FullPathToArray(field);
            for (int i = 0; i < path.Length - 1; i++) {
                State.PushString(path[i]);
                State.GetTable(-2);
            }

            State.PushString(path[path.Length - 1]);
            Translator.Push(val);
            State.SetTable(-3);

            State.SetTop(oldTop);
        }

        /// <summary>
        /// Sets a numeric field of the table or userdata corresponding the the provided reference to the provided value
        /// </summary>
        internal void SetObjectField(int reference, object field, object val) {
            int oldTop = State.GetTop();
            State.GetRef(reference);
            Translator.Push(field);
            Translator.Push(val);
            State.SetTable(-3);
            State.SetTop(oldTop);
        }

        /// <summary>
        /// Compares the two values referenced by ref1 and ref2 for equality
        /// </summary>
        internal bool CompareRef(int ref1, int ref2) {
            int top = State.GetTop();
            State.GetRef(ref1);
            State.GetRef(ref2);
            bool equal = State.Compare(-1, -2, LuaCompare.Equal);
            State.SetTop(top);
            return equal;
        }

        public string DumpStack() {
            int depth = State.GetTop();

            StringBuilder builder = new StringBuilder();
            builder.Append("lua stack depth: ").AppendLine(depth.ToString());

            for (int i = 1; i <= depth; i++) {
                LuaType type = State.Type(i);
                // we dump stacks when deep in calls, calling typename while the stack is in flux can fail sometimes, so manually check for key types
                string typestr = (type == LuaType.Table) ? "table" : State.TypeName(type);
                string strrep = State.ToString(i, false);

                if (type == LuaType.UserData) {
                    object obj = Translator.GetRawNetObject(i);
                    strrep = obj.ToString();
                }

                builder.Append(i).Append(": (").Append(typestr).Append(") ").AppendLine(strrep);
            }

            return builder.ToString();
        }

        #region IDisposable Members

        ~Lua() {
            Dispose();
        }
        public virtual void Dispose() {
            Translator.PendingEvents.Dispose();
            if (Translator.Tag != IntPtr.Zero)
                Marshal.FreeHGlobal(Translator.Tag);

            Close();
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
