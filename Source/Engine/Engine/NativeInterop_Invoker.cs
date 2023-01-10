// Copyright (c) 2012-2023 Wojciech Figat. All rights reserved.

#if USE_NETCORE
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace FlaxEngine
{
    internal unsafe static partial class NativeInterop
    {
        /// <summary>
        /// Helper class for invoking managed methods from delegates.
        /// </summary>
        internal static class Invoker
        {
            internal delegate IntPtr MarshalAndInvokeDelegate(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr);
            internal delegate IntPtr InvokeThunkDelegate(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs);

            /// <summary>
            /// Casts managed pointer to unmanaged pointer.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static T* ToPointer<T>(IntPtr ptr) where T : unmanaged
            {
                return (T*)ptr.ToPointer();
            }
            internal static MethodInfo ToPointerMethod = typeof(Invoker).GetMethod(nameof(Invoker.ToPointer), BindingFlags.Static | BindingFlags.NonPublic);

            /// <summary>
            /// Creates a delegate for invoker to pass parameters as references.
            /// </summary>
            internal static Delegate CreateDelegateFromMethod(MethodInfo method, bool passParametersByRef = true)
            {
                Type[] methodParameters;
                if (method.IsStatic)
                    methodParameters = method.GetParameters().Select(x => x.ParameterType).ToArray();
                else
                    methodParameters = method.GetParameters().Select(x => x.ParameterType).Prepend(method.DeclaringType).ToArray();

                // Pass delegate parameters by reference
                Type[] delegateParameters = methodParameters.Select(x => x.IsPointer ? typeof(IntPtr) : x)
                    .Select(x => passParametersByRef && !x.IsByRef ? x.MakeByRefType() : x).ToArray();
                if (!method.IsStatic && passParametersByRef)
                    delegateParameters[0] = method.DeclaringType;

                // Convert unmanaged pointer parameters to IntPtr
                ParameterExpression[] parameterExpressions = delegateParameters.Select(x => Expression.Parameter(x)).ToArray();
                Expression[] callExpressions = new Expression[methodParameters.Length];
                for (int i = 0; i < methodParameters.Length; i++)
                {
                    Type parameterType = methodParameters[i];
                    if (parameterType.IsPointer)
                    {
                        callExpressions[i] =
                            Expression.Call(null, ToPointerMethod.MakeGenericMethod(parameterType.GetElementType()), parameterExpressions[i]);
                    }
                    else
                        callExpressions[i] = parameterExpressions[i];
                }

                // Create and compile the delegate
                MethodCallExpression callDelegExp;
                if (method.IsStatic)
                    callDelegExp = Expression.Call(null, method, callExpressions.ToArray());
                else
                    callDelegExp = Expression.Call(parameterExpressions[0], method, callExpressions.Skip(1).ToArray());
                Type delegateType = DelegateHelpers.MakeNewCustomDelegate(delegateParameters.Append(method.ReturnType).ToArray());
                return Expression.Lambda(delegateType, callDelegExp, parameterExpressions.ToArray()).Compile();
            }

            internal static IntPtr MarshalReturnValue<TRet>(ref TRet returnValue)
            {
                if (typeof(TRet) == typeof(string))
                    return ManagedString.ToNative(Unsafe.As<string>(returnValue));
                else if (typeof(TRet) == typeof(IntPtr))
                    return (IntPtr)(object)returnValue;
                else if (typeof(TRet) == typeof(ManagedHandle))
                    return ManagedHandle.ToIntPtr((ManagedHandle)(object)returnValue);
                else if (typeof(TRet) == typeof(bool))
                    return (bool)(object)returnValue ? boolTruePtr : boolFalsePtr;
                else if (typeof(TRet) == typeof(Type))
                    return returnValue != null ? ManagedHandle.ToIntPtr(GetTypeGCHandle(Unsafe.As<Type>(returnValue))) : IntPtr.Zero;
                else if (typeof(TRet).IsArray)
                {
                    if (returnValue == null)
                        return IntPtr.Zero;
                    var elementType = typeof(TRet).GetElementType();
                    if (ArrayFactory.GetMarshalledType(elementType) == elementType)
                        return ManagedHandle.ToIntPtr(ManagedHandle.Alloc(ManagedArray.WrapNewArray(Unsafe.As<Array>(returnValue)), GCHandleType.Weak));
                    else
                        return ManagedHandle.ToIntPtr(ManagedHandle.Alloc(ManagedArray.WrapNewArray(ManagedArrayToGCHandleArray(Unsafe.As<Array>(returnValue))), GCHandleType.Weak));
                }
                // Match Mono bindings and pass value as pointer to prevent boxing it
                else if (typeof(TRet) == typeof(System.Int16))
                    return new IntPtr((int)(System.Int16)(object)returnValue);
                else if (typeof(TRet) == typeof(System.Int32))
                    return new IntPtr((int)(System.Int32)(object)returnValue);
                else if (typeof(TRet) == typeof(System.Int64))
                    return new IntPtr((long)(System.Int64)(object)returnValue);
                else if (typeof(TRet) == typeof(System.UInt16))
                    return (IntPtr)new UIntPtr((ulong)(System.UInt16)(object)returnValue);
                else if (typeof(TRet) == typeof(System.UInt32))
                    return (IntPtr)new UIntPtr((ulong)(System.UInt32)(object)returnValue);
                else if (typeof(TRet) == typeof(System.UInt64))
                    return (IntPtr)new UIntPtr((ulong)(System.UInt64)(object)returnValue);
                else
                    return returnValue != null ? ManagedHandle.ToIntPtr(ManagedHandle.Alloc(returnValue, GCHandleType.Weak)) : IntPtr.Zero;
            }

            internal static class InvokerNoRet0<TInstance>
            {
                internal delegate void InvokerDelegate(object instance);
                internal delegate void ThunkInvokerDelegate(object instance);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    deleg(instancePtr.Target);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    deleg(instancePtr.Target);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerNoRet1<TInstance, T1>
            {
                internal delegate void InvokerDelegate(object instance, ref T1 param1);
                internal delegate void ThunkInvokerDelegate(object instance, T1 param1);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);

                    T1 param1 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);

                    deleg(instancePtr.Target, ref param1);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);

                    deleg(instancePtr.Target, param1);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerNoRet2<TInstance, T1, T2>
            {
                internal delegate void InvokerDelegate(object instance, ref T1 param1, ref T2 param2);
                internal delegate void ThunkInvokerDelegate(object instance, T1 param1, T2 param2);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);

                    deleg(instancePtr.Target, ref param1, ref param2);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);

                    deleg(instancePtr.Target, param1, param2);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerNoRet3<TInstance, T1, T2, T3>
            {
                internal delegate void InvokerDelegate(object instance, ref T1 param1, ref T2 param2, ref T3 param3);
                internal delegate void ThunkInvokerDelegate(object instance, T1 param1, T2 param2, T3 param3);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);

                    deleg(instancePtr.Target, ref param1, ref param2, ref param3);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);

                    deleg(instancePtr.Target, param1, param2, param3);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerNoRet4<TInstance, T1, T2, T3, T4>
            {
                internal delegate void InvokerDelegate(object instance, ref T1 param1, ref T2 param2, ref T3 param3, ref T4 param4);
                internal delegate void ThunkInvokerDelegate(object instance, T1 param1, T2 param2, T3 param3, T4 param4);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);
                    IntPtr param4Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    T4 param4 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);
                    if (param4Ptr != IntPtr.Zero) MarshalHelper<T4>.ToManaged(ref param4, param4Ptr, types[3].IsByRef);

                    deleg(instancePtr.Target, ref param1, ref param2, ref param3, ref param4);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);
                    if (types[3].IsByRef) MarshalHelper<T4>.ToNative(ref param4, param4Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);
                    T4 param4 = MarshalHelper<T4>.ToManagedUnbox(paramPtrs[3]);

                    deleg(instancePtr.Target, param1, param2, param3, param4);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerStaticNoRet0
            {
                internal delegate void InvokerDelegate();
                internal delegate void ThunkInvokerDelegate();

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    deleg();

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    deleg();

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerStaticNoRet1<T1>
            {
                internal delegate void InvokerDelegate(ref T1 param1);
                internal delegate void ThunkInvokerDelegate(T1 param1);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);

                    T1 param1 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);

                    deleg(ref param1);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);

                    deleg(param1);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerStaticNoRet2<T1, T2>
            {
                internal delegate void InvokerDelegate(ref T1 param1, ref T2 param2);
                internal delegate void ThunkInvokerDelegate(T1 param1, T2 param2);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);

                    deleg(ref param1, ref param2);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);

                    deleg(param1, param2);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerStaticNoRet3<T1, T2, T3>
            {
                internal delegate void InvokerDelegate(ref T1 param1, ref T2 param2, ref T3 param3);
                internal delegate void ThunkInvokerDelegate(T1 param1, T2 param2, T3 param3);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);

                    deleg(ref param1, ref param2, ref param3);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);

                    deleg(param1, param2, param3);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerStaticNoRet4<T1, T2, T3, T4>
            {
                internal delegate void InvokerDelegate(ref T1 param1, ref T2 param2, ref T3 param3, ref T4 param4);
                internal delegate void ThunkInvokerDelegate(T1 param1, T2 param2, T3 param3, T4 param4);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);
                    IntPtr param4Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    T4 param4 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);
                    if (param4Ptr != IntPtr.Zero) MarshalHelper<T4>.ToManaged(ref param4, param4Ptr, types[3].IsByRef);

                    deleg(ref param1, ref param2, ref param3, ref param4);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);
                    if (types[3].IsByRef) MarshalHelper<T4>.ToNative(ref param4, param4Ptr);

                    return IntPtr.Zero;
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);
                    T4 param4 = MarshalHelper<T4>.ToManagedUnbox(paramPtrs[3]);

                    deleg(param1, param2, param3, param4);

                    return IntPtr.Zero;
                }
            }

            internal static class InvokerRet0<TInstance, TRet>
            {
                internal delegate TRet InvokerDelegate(object instance);
                internal delegate TRet ThunkInvokerDelegate(object instance);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    TRet ret = deleg(instancePtr.Target);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    TRet ret = deleg(instancePtr.Target);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerRet1<TInstance, TRet, T1>
            {
                internal delegate TRet InvokerDelegate(object instance, ref T1 param1);
                internal delegate TRet ThunkInvokerDelegate(object instance, T1 param1);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);

                    T1 param1 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);

                    TRet ret = deleg(instancePtr.Target, ref param1);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);

                    TRet ret = deleg(instancePtr.Target, param1);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerRet2<TInstance, TRet, T1, T2>
            {
                internal delegate TRet InvokerDelegate(object instance, ref T1 param1, ref T2 param2);
                internal delegate TRet ThunkInvokerDelegate(object instance, T1 param1, T2 param2);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);

                    TRet ret = deleg(instancePtr.Target, ref param1, ref param2);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);

                    TRet ret = deleg(instancePtr.Target, param1, param2);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerRet3<TInstance, TRet, T1, T2, T3>
            {
                internal delegate TRet InvokerDelegate(object instance, ref T1 param1, ref T2 param2, ref T3 param3);
                internal delegate TRet ThunkInvokerDelegate(object instance, T1 param1, T2 param2, T3 param3);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);

                    TRet ret = deleg(instancePtr.Target, ref param1, ref param2, ref param3);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);

                    TRet ret = deleg(instancePtr.Target, param1, param2, param3);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerRet4<TInstance, TRet, T1, T2, T3, T4>
            {
                internal delegate TRet InvokerDelegate(object instance, ref T1 param1, ref T2 param2, ref T3 param3, ref T4 param4);
                internal delegate TRet ThunkInvokerDelegate(object instance, T1 param1, T2 param2, T3 param3, T4 param4);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);
                    IntPtr param4Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    T4 param4 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);
                    if (param4Ptr != IntPtr.Zero) MarshalHelper<T4>.ToManaged(ref param4, param4Ptr, types[3].IsByRef);

                    TRet ret = deleg(instancePtr.Target, ref param1, ref param2, ref param3, ref param4);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);
                    if (types[3].IsByRef) MarshalHelper<T4>.ToNative(ref param4, param4Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);
                    T4 param4 = MarshalHelper<T4>.ToManagedUnbox(paramPtrs[3]);

                    TRet ret = deleg(instancePtr.Target, param1, param2, param3, param4);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerStaticRet0<TRet>
            {
                internal delegate TRet InvokerDelegate();
                internal delegate TRet ThunkInvokerDelegate();

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    TRet ret = deleg();

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    TRet ret = deleg();

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerStaticRet1<TRet, T1>
            {
                internal delegate TRet InvokerDelegate(ref T1 param1);
                internal delegate TRet ThunkInvokerDelegate(T1 param1);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);

                    T1 param1 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);

                    TRet ret = deleg(ref param1);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);

                    TRet ret = deleg(param1);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerStaticRet2<TRet, T1, T2>
            {
                internal delegate TRet InvokerDelegate(ref T1 param1, ref T2 param2);
                internal delegate TRet ThunkInvokerDelegate(T1 param1, T2 param2);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);

                    TRet ret = deleg(ref param1, ref param2);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);

                    TRet ret = deleg(param1, param2);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerStaticRet3<TRet, T1, T2, T3>
            {
                internal delegate TRet InvokerDelegate(ref T1 param1, ref T2 param2, ref T3 param3);
                internal delegate TRet ThunkInvokerDelegate(T1 param1, T2 param2, T3 param3);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);

                    TRet ret = deleg(ref param1, ref param2, ref param3);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);

                    TRet ret = deleg(param1, param2, param3);

                    return MarshalReturnValue(ref ret);
                }
            }

            internal static class InvokerStaticRet4<TRet, T1, T2, T3, T4>
            {
                internal delegate TRet InvokerDelegate(ref T1 param1, ref T2 param2, ref T3 param3, ref T4 param4);
                internal delegate TRet ThunkInvokerDelegate(T1 param1, T2 param2, T3 param3, T4 param4);

                internal static object CreateDelegate(MethodInfo method)
                {
                    return new Tuple<Type[], InvokerDelegate>(method.GetParameters().Select(x => x.ParameterType).ToArray(), Unsafe.As<InvokerDelegate>(CreateDelegateFromMethod(method)));
                }

                internal static object CreateInvokerDelegate(MethodInfo method)
                {
                    return Unsafe.As<ThunkInvokerDelegate>(CreateDelegateFromMethod(method, false));
                }

                [DebuggerStepThrough]
                internal static IntPtr MarshalAndInvoke(object delegateContext, ManagedHandle instancePtr, IntPtr paramPtr)
                {
                    (Type[] types, InvokerDelegate deleg) = (Tuple<Type[], InvokerDelegate>)(delegateContext);

                    IntPtr param1Ptr = Marshal.ReadIntPtr(paramPtr);
                    IntPtr param2Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size);
                    IntPtr param3Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size);
                    IntPtr param4Ptr = Marshal.ReadIntPtr(paramPtr + IntPtr.Size + IntPtr.Size + IntPtr.Size);

                    T1 param1 = default;
                    T2 param2 = default;
                    T3 param3 = default;
                    T4 param4 = default;
                    if (param1Ptr != IntPtr.Zero) MarshalHelper<T1>.ToManaged(ref param1, param1Ptr, types[0].IsByRef);
                    if (param2Ptr != IntPtr.Zero) MarshalHelper<T2>.ToManaged(ref param2, param2Ptr, types[1].IsByRef);
                    if (param3Ptr != IntPtr.Zero) MarshalHelper<T3>.ToManaged(ref param3, param3Ptr, types[2].IsByRef);
                    if (param4Ptr != IntPtr.Zero) MarshalHelper<T4>.ToManaged(ref param4, param4Ptr, types[3].IsByRef);

                    TRet ret = deleg(ref param1, ref param2, ref param3, ref param4);

                    // Marshal reference parameters back to original unmanaged references
                    if (types[0].IsByRef) MarshalHelper<T1>.ToNative(ref param1, param1Ptr);
                    if (types[1].IsByRef) MarshalHelper<T2>.ToNative(ref param2, param2Ptr);
                    if (types[2].IsByRef) MarshalHelper<T3>.ToNative(ref param3, param3Ptr);
                    if (types[3].IsByRef) MarshalHelper<T4>.ToNative(ref param4, param4Ptr);

                    return MarshalReturnValue(ref ret);
                }

                [DebuggerStepThrough]
                internal static unsafe IntPtr InvokeThunk(object delegateContext, ManagedHandle instancePtr, IntPtr* paramPtrs)
                {
                    ThunkInvokerDelegate deleg = Unsafe.As<ThunkInvokerDelegate>(delegateContext);

                    T1 param1 = MarshalHelper<T1>.ToManagedUnbox(paramPtrs[0]);
                    T2 param2 = MarshalHelper<T2>.ToManagedUnbox(paramPtrs[1]);
                    T3 param3 = MarshalHelper<T3>.ToManagedUnbox(paramPtrs[2]);
                    T4 param4 = MarshalHelper<T4>.ToManagedUnbox(paramPtrs[3]);

                    TRet ret = deleg(param1, param2, param3, param4);

                    return MarshalReturnValue(ref ret);
                }
            }
        }
    }
}

#endif
