using System;
using System.Threading.Tasks;

namespace protobuf_net.Grpc.Test
{
    public class LegacyService : ILegacyService
    {
        public HelloReply Shared_Legacy_BlockingUnary(string arg1, long arg2) =>
            new HelloReply { Message = $"Hello {arg1}, aged {arg2}"};

        public HelloReply Shared_Legacy_BlockingUnary_ManyArgs(string arg1, long arg2, HelloRequest arg3, string arg4, long arg5,
            HelloRequest arg6, string arg7, long arg8, HelloRequest arg9) =>
            new HelloReply { Message = $"Hello {arg1}, aged {arg2}"};

        public long Shared_Legacy_BlockingUnary_VoidValueType() => 42;

        public long Shared_Legacy_BlockingUnary_ValueTypeValueType(DateTime arg) => 42;

        public void Shared_Legacy_BlockingUnary_ValueTypeVoid(long arg) { }

        public void Shared_Legacy_BlockingUnary_ValVoid(string arg1, long arg2) { }

        public Task<HelloReply> Shared_Legacy_TaskUnary(string arg1, long arg2) =>
            Task.FromResult(new HelloReply { Message = $"Hello {arg1}, aged {arg2}"});

        public Task Shared_Legacy_TaskUnary_ValVoid(string arg1, long arg2) => Task.CompletedTask;

        public ValueTask<HelloReply> Shared_Legacy_ValueTaskUnary(string arg1, long arg2) =>
            new ValueTask<HelloReply>(new HelloReply { Message = $"Hello {arg1}, aged {arg2}"});

        public ValueTask Shared_Legacy_ValueTaskUnary_ValVoid(string arg1, long arg2) => new ValueTask();

        public Task Shared_Legacy_TaskUnary_ValueTypeVoid(long arg) => Task.CompletedTask;

        public Task<long> Shared_Legacy_TaskUnary_ValueTypeValueType(DateTime arg) => Task.FromResult(42L);

        public Task<long> Shared_Legacy_TaskUnary_VoidValueType() => Task.FromResult(42L);

        public ValueTask Shared_Legacy_ValueTaskUnary_ValueTypeVoid(long arg) => new ValueTask();

        public ValueTask<long> Shared_Legacy_ValueTaskUnary_ValueTypeValueType(DateTime arg) => new ValueTask<long>(42);

        public ValueTask<long> Shared_Legacy_ValueTaskUnary_VoidValueType() => new ValueTask<long>(42);
    }
}
