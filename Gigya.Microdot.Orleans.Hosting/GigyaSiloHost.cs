#region Copyright

// Copyright 2017 Gigya Inc.  All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDER AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.

#endregion Copyright

using Gigya.Common.Contracts.Exceptions;
using Gigya.Microdot.Hosting.HttpService;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.Orleans.Hosting.Logging;
using Gigya.Microdot.SharedLogic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using Gigya.Microdot.SharedLogic.Events;

namespace Gigya.Microdot.Orleans.Hosting
{
    public interface IServiceProviderInit
    {
        IServiceProvider ConfigureServices(IServiceCollection services);
    }

    public class GigyaSiloHost
    {
        private readonly IServiceProviderInit _serviceProvider;
        private readonly OrleansLogProvider _logProvider;
        private readonly OrleansConfigurationBuilder _orleansConfigurationBuilder;
        private readonly MicrodotIncomingGrainCallFilter _callFilter;
        public static IGrainFactory GrainFactory { get; private set; }
        private Exception _startupTaskExceptions { get; set; }
        private Func<IGrainFactory, Task> AfterOrleansStartup { get; set; }
        private ILog Log { get; }
        private HttpServiceListener HttpServiceListener { get; }
        private ServiceArguments _serviceArguments = new ServiceArguments();
        public GigyaSiloHost(ILog log,
            HttpServiceListener httpServiceListener,
         IServiceProviderInit serviceProvider, OrleansLogProvider logProvider, OrleansConfigurationBuilder orleansConfigurationBuilder, MicrodotIncomingGrainCallFilter callFilter)

        {
            _serviceProvider = serviceProvider;
            _logProvider = logProvider;
            _orleansConfigurationBuilder = orleansConfigurationBuilder;
            _callFilter = callFilter;
            Log = log;
            HttpServiceListener = httpServiceListener;
        }

        private ISiloHost _siloHost;

        public void Start(ServiceArguments serviceArguments, Func<IGrainFactory, Task> afterOrleansStartup = null,
            Func<IGrainFactory, Task> beforeOrleansShutdown = null)
        {
            AfterOrleansStartup = afterOrleansStartup;
            _serviceArguments = serviceArguments;

            Log.Info(_ => _("Starting Orleans silo..."));

            _siloHost = _orleansConfigurationBuilder.GetBuilder()
               .UseServiceProviderFactory(_serviceProvider.ConfigureServices)
               .ConfigureLogging(op => op.AddProvider(_logProvider))
               .AddStartupTask(StartupTask)
               .AddIncomingGrainCallFilter(async (o) => { await _callFilter.Invoke(o); })
               .AddOutgoingGrainCallFilter(async (o) =>
               {
                   TracingContext.SetUpStorage();
                   TracingContext.SpanStartTime = DateTimeOffset.UtcNow;
                   await o.Invoke();
               })

               .Build();

            try
            {
                int cancelAfter = _serviceArguments.InitTimeOutSec ?? 60;
                var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(cancelAfter)).Token;
                var forceCancel = new CancellationTokenSource(TimeSpan.FromSeconds(cancelAfter + 10)).Token;

                _siloHost.StartAsync(cancel).Wait(forceCancel);
            }
            catch (Exception e)
            {
                throw new ProgrammaticException("Failed to start Orleans silo", unencrypted: new Tags { { "siloName", CurrentApplicationInfo.HostName } }, innerException: e);
            }

            if (_startupTaskExceptions != null)
                throw new ProgrammaticException("Failed to start Orleans silo due to an exception thrown in the bootstrap method.", unencrypted: new Tags { { "siloName", CurrentApplicationInfo.HostName } }, innerException: _startupTaskExceptions);

            Log.Info(_ => _("Successfully started Orleans silo", unencryptedTags: new { siloName = CurrentApplicationInfo.HostName }));
        }

        public void Stop()
        {
            if (_serviceArguments == null)
                return;

            var cancelAfter = new CancellationTokenSource(TimeSpan.FromSeconds(_serviceArguments.OnStopWaitTimeSec.Value)).Token;

            try
            {
                HttpServiceListener?.Dispose();
            }
            catch (Exception e)
            {
                Log.Warn((m) => m("Failed to close HttpServiceListener ", exception: e));
            }

            try
            {
                _siloHost?.StopAsync(cancelAfter).Wait(cancelAfter);
            }
            catch (Exception e)
            {
                Log.Error((m) => m(" Silo failed to StopAsync", exception: e));
            }
        }

        private async Task StartupTask(IServiceProvider serviceProvider, CancellationToken arg2)
        {
            GrainTaskScheduler = TaskScheduler.Current;
            GrainFactory = serviceProvider.GetService<IGrainFactory>();

            try
            {
                if (AfterOrleansStartup != null)
                    await AfterOrleansStartup(GrainFactory);
            }
            catch (Exception ex)
            {
                _startupTaskExceptions = ex;
                throw;
            }

            try
            {
                HttpServiceListener.Start();
            }
            catch (Exception ex)
            {
                _startupTaskExceptions = ex;
                Log.Error("Failed to start HttpServiceListener", exception: ex);
                throw;
            }
        }

        public TaskScheduler GrainTaskScheduler { get; set; }
    }
}