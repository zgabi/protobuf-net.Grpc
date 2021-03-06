﻿using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ProtoBuf.Grpc.Internal
{
    internal static class ServerInvokerLookup
    {
        internal static int GeneralPurposeSignatureCount() => _invokers.Keys.Count(x => x.Context == ContextKind.CallContext || x.Context == ContextKind.NoContext || x.Context == ContextKind.CancellationToken);

        static Expression ToTaskT(Expression expression)
        {
            var type = expression.Type;
            if (type == typeof(void))
            {
                // no result from the call; add in Empty.Instance instead
                var field = Expression.Field(null, ProxyEmitter.s_Empty_InstaneTask);
                return Expression.Block(expression, field);
            }
#pragma warning disable CS0618 // Reshape
            if (type == typeof(ValueTask))
                return Expression.Call(typeof(Reshape), nameof(Reshape.EmptyValueTask), null, expression);
            if (type == typeof(Task))
                return Expression.Call(typeof(Reshape), nameof(Reshape.EmptyTask), null, expression);
#pragma warning restore CS0618

            if (type.IsGenericType)
            {
                if (type.GetGenericTypeDefinition() == typeof(Task<>))
                    return expression;
                if (type.GetGenericTypeDefinition() == typeof(ValueTask<>))
                    return Expression.Call(expression, nameof(ValueTask<int>.AsTask), null);
            }
            return Expression.Call(typeof(Task), nameof(Task.FromResult), new Type[] { expression.Type }, expression);
        }

        static Expression TupleInDeconstruct(Expression instance, MethodInfo method, params Expression[] args)
        {
            var parameters = method.GetParameters();
            int callContext = parameters[parameters.Length - 1].ParameterType == typeof(CallContext) ? 1 : 0;
            int cancellationToken = parameters[parameters.Length - 1].ParameterType == typeof(CancellationToken) ? 1 : 0;
            Expression callExpression;
            if (parameters.Length == callContext + cancellationToken + 1)
            {
                if (parameters[0].ParameterType.IsClass)
                {
                    callExpression = Expression.Call(instance, method, args);
                }
                else
                {
                    // then the parameter should be wrapped in a ValueTypeWrapper
                    var valueField = args[0].Type.GetField("Value");
                    var unwrappedArg = Expression.Field(args[0], valueField);
                    var actualArgs = new[] { unwrappedArg }.Concat(args.Skip(1)).ToArray();

                    callExpression = Expression.Call(instance, method, actualArgs);
                }
            }
            else
            {
                // assume args[0] is a Tuple with appropriate elements
                var actualArgs = Enumerable.Range(0, parameters.Length - args.Length + 1)
                    .Select(i => GetNthItem(args[0], i))
                    .Concat(args.Skip(1))
                    .ToArray();

                callExpression = Expression.Call(instance, method, actualArgs);
            }

            return ValueTypeWrapperOutConstruct(callExpression);

            Expression GetNthItem(Expression tuple, int n) => n < 7
                ? Expression.Property(tuple, tuple.Type.GetProperty($"Item{n + 1}") ??
                                             throw new InvalidOperationException($"No property Item{n + 1} found on {tuple.Type.FullName}"))
                : GetNthItem(Expression.Property(tuple, tuple.Type.GetProperty("Rest") ??
                                                        throw new InvalidOperationException($"No property Rest found on {tuple.Type.FullName}")), n - 7);
        }

        static Expression ValueTypeWrapperOutConstruct(Expression callResult)
        {
            if (callResult.Type == typeof(void) || callResult.Type == typeof(Task) || callResult.Type == typeof(ValueTask) ||
                (callResult.Type.IsGenericType && typeof(IAsyncEnumerable<>).IsAssignableFrom(callResult.Type.GetGenericTypeDefinition())))
                return callResult;

            if (callResult.Type.IsGenericType &&
                (callResult.Type.GetGenericTypeDefinition() == typeof(Task<>) ||
                 callResult.Type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            {
                var underlyingType = callResult.Type.GetGenericArguments().First();
                if (underlyingType.IsClass)
                    return callResult;

                var isValueTask = callResult.Type.GetGenericTypeDefinition() == typeof(ValueTask<>);
                Type valueTypeWrapperType = typeof(ValueTypeWrapper<>).MakeGenericType(underlyingType);
                var taskType = typeof(Task<>).MakeGenericType(underlyingType);
                var taskParam = Expression.Parameter(taskType, "t");
                var valueTypeWrapperConstructor = valueTypeWrapperType.GetConstructor(new[] { underlyingType });
                var resultProperty = taskType.GetProperty("Result");
                var lambdaBody = Expression.New(valueTypeWrapperConstructor, Expression.Property(taskParam, resultProperty));
                var continuationLambda = Expression.Lambda(lambdaBody, taskParam);
                var task = isValueTask ? Expression.Call(callResult, callResult.Type.GetMethod(nameof(ValueTask.AsTask))) : callResult;
                var continueWithMethod = ReflectionHelper.GetContinueWithForTask(underlyingType, valueTypeWrapperType);
                var continuation = Expression.Call(task, continueWithMethod, continuationLambda);

                if (!isValueTask)
                    return continuation;

                var targetValueTaskType = typeof(ValueTask<>).MakeGenericType(valueTypeWrapperType);
                var targetValueTaskConstructor = targetValueTaskType.GetConstructor(new[] { typeof(Task<>).MakeGenericType(valueTypeWrapperType) });
                return Expression.New(targetValueTaskConstructor, continuation);
            }

            if (callResult.Type.IsClass)
                return callResult;

            var valueTypeWrapperReturnTypeConstructor = typeof(ValueTypeWrapper<>).MakeGenericType(callResult.Type).GetConstructor(new[] { callResult.Type });
            return Expression.New(valueTypeWrapperReturnTypeConstructor, callResult);
        }
        
        internal static readonly ConstructorInfo s_CallContext_FromServerContext = typeof(CallContext).GetConstructor(new[] { typeof(object), typeof(ServerCallContext) })!;
        internal static readonly PropertyInfo s_ServerContext_CancellationToken = typeof(ServerCallContext).GetProperty(nameof(ServerCallContext.CancellationToken))!;

        static Expression ToCallContext(Expression server, Expression context) => Expression.New(s_CallContext_FromServerContext, server, context);
        static Expression ToCancellationToken(Expression context) => Expression.Property(context, s_ServerContext_CancellationToken);

#pragma warning disable CS0618 // Reshape
        static Expression AsAsyncEnumerable(Expression value, Expression context)
            => Expression.Call(typeof(Reshape), nameof(Reshape.AsAsyncEnumerable),
                typeArguments: value.Type.GetGenericArguments(),
                arguments: new Expression[] { value, Expression.Property(context, nameof(ServerCallContext.CancellationToken)) });

        static Expression WriteTo(Expression value, Expression writer, Expression context)
            => Expression.Call(typeof(Reshape), nameof(Reshape.WriteTo),
                typeArguments: value.Type.GetGenericArguments(),
                arguments: new Expression[] { value, writer, Expression.Property(context, nameof(ServerCallContext.CancellationToken)) });

        internal static bool TryGetValue(MethodType MethodType, ContextKind Context, ResultKind Result, VoidKind Void, out Func<MethodInfo, Expression[], Expression>? invoker)
            => _invokers.TryGetValue((MethodType, Context, Result, Void), out invoker);

#pragma warning restore CS0618

        private static readonly Dictionary<(MethodType Method, ContextKind Context, ResultKind Result, VoidKind Void), Func<MethodInfo, Expression[], Expression>?> _invokers
            = new Dictionary<(MethodType, ContextKind, ResultKind, VoidKind), Func<MethodInfo, Expression[], Expression>?>
        {
                // GRPC-style server methods are direct match; no mapping required
                // => service.{method}(args)
                { (MethodType.Unary, ContextKind.ServerCallContext, ResultKind.Task, VoidKind.None), null },
                { (MethodType.ServerStreaming, ContextKind.ServerCallContext, ResultKind.Task, VoidKind.None), null },
                { (MethodType.ClientStreaming, ContextKind.ServerCallContext, ResultKind.Task, VoidKind.None), null },
                { (MethodType.DuplexStreaming, ContextKind.ServerCallContext, ResultKind.Task, VoidKind.None), null },

                // Unary: Task<TResponse> Foo(TService service, TRequest request, ServerCallContext serverCallContext);
                // => service.{method}(request, [new CallContext(serverCallContext)])
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Task, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1])) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.ValueTask, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1])) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Sync, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1])) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Task, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.ValueTask, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Sync, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Task, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCancellationToken(args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.ValueTask, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCancellationToken(args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Sync, VoidKind.None), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCancellationToken(args[2]))) },

                
                // Unary: Task<TResponse> Foo(TService service, TRequest request, ServerCallContext serverCallContext);
                // => service.{method}(request, [new CallContext(serverCallContext)]) return Empty.Instance;
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Task, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1])) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.ValueTask, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1])) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Sync, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1])) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Task, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.ValueTask, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Sync, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Task, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCancellationToken(args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.ValueTask, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCancellationToken(args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Sync, VoidKind.Response), (method, args) => ToTaskT(TupleInDeconstruct(args[0], method, args[1], ToCancellationToken(args[2]))) },

                // Unary: Task<TResponse> Foo(TService service, TRequest request, ServerCallContext serverCallContext);
                // => service.{method}([new CallContext(serverCallContext)])
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Task, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method))) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.ValueTask, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method))) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Sync, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Task, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method, ToCallContext(args[0], args[2])))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.ValueTask, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method, ToCallContext(args[0], args[2])))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Sync, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method, ToCallContext(args[0], args[2])))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Task, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method, ToCancellationToken(args[2])))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.ValueTask, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method, ToCancellationToken(args[2])))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Sync, VoidKind.Request), (method, args) => ToTaskT(ValueTypeWrapperOutConstruct(Expression.Call(args[0], method, ToCancellationToken(args[2])))) },

                
                // Unary: Task<TResponse> Foo(TService service, TRequest request, ServerCallContext serverCallContext);
                // => service.{method}([new CallContext(serverCallContext)]) return Empty.Instance;
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Task, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method)) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.ValueTask, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method)) },
                {(MethodType.Unary, ContextKind.NoContext, ResultKind.Sync, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method)) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Task, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method, ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.ValueTask, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method, ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CallContext, ResultKind.Sync, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method, ToCallContext(args[0], args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Task, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method, ToCancellationToken(args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.ValueTask, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method, ToCancellationToken(args[2]))) },
                {(MethodType.Unary, ContextKind.CancellationToken, ResultKind.Sync, VoidKind.Both), (method, args) => ToTaskT(Expression.Call(args[0], method, ToCancellationToken(args[2]))) },

                // Client Streaming: Task<TResponse> Foo(TService service, IAsyncStreamReader<TRequest> stream, ServerCallContext serverCallContext);
                // => service.{method}(reader.AsAsyncEnumerable(serverCallContext.CancellationToken), [new CallContext(serverCallContext)])
                {(MethodType.ClientStreaming, ContextKind.NoContext, ResultKind.Task, VoidKind.None), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]))) },
                {(MethodType.ClientStreaming, ContextKind.NoContext, ResultKind.ValueTask, VoidKind.None), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]))) },

                {(MethodType.ClientStreaming, ContextKind.CallContext, ResultKind.Task, VoidKind.None), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCallContext(args[0], args[2]))) },
                {(MethodType.ClientStreaming, ContextKind.CallContext, ResultKind.ValueTask, VoidKind.None), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCallContext(args[0], args[2]))) },

                {(MethodType.ClientStreaming, ContextKind.CancellationToken, ResultKind.Task, VoidKind.None), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCancellationToken(args[2]))) },
                {(MethodType.ClientStreaming, ContextKind.CancellationToken, ResultKind.ValueTask, VoidKind.None), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCancellationToken(args[2]))) },

                {(MethodType.ClientStreaming, ContextKind.NoContext, ResultKind.Task, VoidKind.Response), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]))) },
                {(MethodType.ClientStreaming, ContextKind.NoContext, ResultKind.ValueTask, VoidKind.Response), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]))) },

                {(MethodType.ClientStreaming, ContextKind.CallContext, ResultKind.Task, VoidKind.Response), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCallContext(args[0], args[2]))) },
                {(MethodType.ClientStreaming, ContextKind.CallContext, ResultKind.ValueTask, VoidKind.Response), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCallContext(args[0], args[2]))) },

                {(MethodType.ClientStreaming, ContextKind.CancellationToken, ResultKind.Task, VoidKind.Response), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCancellationToken(args[2]))) },
                {(MethodType.ClientStreaming, ContextKind.CancellationToken, ResultKind.ValueTask, VoidKind.Response), (method, args) => ToTaskT(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[2]), ToCancellationToken(args[2]))) },

                // Server Streaming: Task Foo(TService service, TRequest request, IServerStreamWriter<TResponse> stream, ServerCallContext serverCallContext);
                // => service.{method}(request, [new CallContext(serverCallContext)]).WriteTo(stream, serverCallContext.CancellationToken)
                {(MethodType.ServerStreaming, ContextKind.NoContext, ResultKind.AsyncEnumerable, VoidKind.None), (method, args) => WriteTo(TupleInDeconstruct(args[0], method, args[1]), args[2], args[3])},
                {(MethodType.ServerStreaming, ContextKind.CallContext, ResultKind.AsyncEnumerable, VoidKind.None), (method, args) => WriteTo(TupleInDeconstruct(args[0], method, args[1], ToCallContext(args[0], args[3])), args[2], args[3])},
                {(MethodType.ServerStreaming, ContextKind.CancellationToken, ResultKind.AsyncEnumerable, VoidKind.None), (method, args) => WriteTo(TupleInDeconstruct(args[0], method, args[1], ToCancellationToken(args[3])), args[2], args[3])},

                {(MethodType.ServerStreaming, ContextKind.NoContext, ResultKind.AsyncEnumerable, VoidKind.Request), (method, args) => WriteTo(Expression.Call(args[0], method), args[2], args[3])},
                {(MethodType.ServerStreaming, ContextKind.CallContext, ResultKind.AsyncEnumerable, VoidKind.Request), (method, args) => WriteTo(Expression.Call(args[0], method, ToCallContext(args[0], args[3])), args[2], args[3])},
                {(MethodType.ServerStreaming, ContextKind.CancellationToken, ResultKind.AsyncEnumerable, VoidKind.Request), (method, args) => WriteTo(Expression.Call(args[0], method, ToCancellationToken(args[3])), args[2], args[3])},

                // Duplex: Task Foo(TService service, IAsyncStreamReader<TRequest> input, IServerStreamWriter<TResponse> output, ServerCallContext serverCallContext);
                // => service.{method}(input.AsAsyncEnumerable(serverCallContext.CancellationToken), [new CallContext(serverCallContext)]).WriteTo(output, serverCallContext.CancellationToken)
                {(MethodType.DuplexStreaming, ContextKind.NoContext, ResultKind.AsyncEnumerable, VoidKind.None), (method, args) => WriteTo(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[3])), args[2], args[3]) },
                {(MethodType.DuplexStreaming, ContextKind.CallContext, ResultKind.AsyncEnumerable, VoidKind.None), (method, args) => WriteTo(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[3]), ToCallContext(args[0], args[3])), args[2], args[3]) },
                {(MethodType.DuplexStreaming, ContextKind.CancellationToken, ResultKind.AsyncEnumerable, VoidKind.None), (method, args) => WriteTo(Expression.Call(args[0], method, AsAsyncEnumerable(args[1], args[3]), ToCancellationToken(args[3])), args[2], args[3]) },
        };
    }
}
