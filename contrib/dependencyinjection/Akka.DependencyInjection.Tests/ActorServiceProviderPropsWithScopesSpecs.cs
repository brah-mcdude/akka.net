﻿//-----------------------------------------------------------------------
// <copyright file="ActorServiceProviderPropsWithScopesSpecs.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2021 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2021 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Linq;
using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;
using Akka.TestKit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Akka.DependencyInjection.Tests
{

    public class ActorServiceProviderPropsWithScopesSpecs : AkkaSpec, IClassFixture<AkkaDiFixture>
    {
        public ActorServiceProviderPropsWithScopesSpecs(AkkaDiFixture fixture, ITestOutputHelper output) : base(ServiceProviderSetup.Create(fixture.Provider)
            .And(BootstrapSetup.Create().WithConfig(TestKitBase.DefaultConfig)), output)
        {

        }

        [Fact(DisplayName = "DI: actors who receive an IServiceScope through Props should dispose of their dependencies upon termination")]
        public void ActorsWithScopedDependenciesShouldDisposeUponStop()
        {
            var spExtension = ServiceProvider.For(Sys);
            var props = spExtension.Props<ScopedActor>();

            // create a scoped actor using the props from Akka.DependencyInjection
            var scoped1 = Sys.ActorOf(props, "scoped1");
            scoped1.Tell(new FetchDependencies());
            var deps1 = ExpectMsg<CurrentDependencies>();
            deps1.Dependencies.All(x => x.Disposed).Should().BeFalse();

            // kill it
            Watch(scoped1);
            scoped1.Tell(PoisonPill.Instance);
            ExpectTerminated(scoped1);

            // all dependencies should be disposed
            AwaitAssert(() =>
            {
                deps1.Dependencies.All(x => x.Disposed).Should().BeTrue();
            });
            

            // reuse the same props
            var scoped2 = Sys.ActorOf(props, "scoped2");
            scoped2.Tell(new FetchDependencies());
            var deps2 = ExpectMsg<CurrentDependencies>();

            // all dependencies should be current, because new scope was generated by Props
            deps2.Dependencies.All(x => x.Disposed).Should().BeFalse();
        }

        [Fact(DisplayName =
            "DI: actors who receive an IServiceScope through Props should dispose of their dependencies and recreate upon restart")]
        public void ActorsWithScopedDependenciesShouldDisposeAndRecreateUponRestart()
        {
            var spExtension = ServiceProvider.For(Sys);
            var props = spExtension.Props<ScopedActor>();

            // create a scoped actor using the props from Akka.DependencyInjection
            var scoped1 = Sys.ActorOf(props, "scoped1");
            scoped1.Tell(new FetchDependencies());
            var deps1 = ExpectMsg<CurrentDependencies>();
            deps1.Dependencies.All(x => x.Disposed).Should().BeFalse();

            // crash + restart it
            EventFilter.Exception<ApplicationException>().ExpectOne(() =>
            {
                scoped1.Tell(new Crash());
            });

            // all previous dependencies should be disposed
            AwaitAssert(() =>
            {
                deps1.Dependencies.All(x => x.Disposed).Should().BeTrue();
            });

            // actor should restart with totally new dependencies
            scoped1.Tell(new FetchDependencies());
            var deps2 = ExpectMsg<CurrentDependencies>();
            deps2.Dependencies.All(x => x.Disposed).Should().BeFalse();
        }

        [Fact(DisplayName =
            "DI: actors who receive a mix of dependencies via IServiceScope should dispose ONLY of their scoped dependencies and recreate upon restart")]
        public void ActorsWithMixedDependenciesShouldDisposeAndRecreateScopedUponRestart()
        {
            var spExtension = ServiceProvider.For(Sys);
            var props = spExtension.Props<MixedActor>();

            // create a scoped actor using the props from Akka.DependencyInjection
            var scoped1 = Sys.ActorOf(props, "scoped1");
            scoped1.Tell(new FetchDependencies());
            var deps1 = ExpectMsg<CurrentDependencies>();
            deps1.Dependencies.All(x => x.Disposed).Should().BeFalse();

            // crash + restart it
            EventFilter.Exception<ApplicationException>().ExpectOne(() =>
            {
                scoped1.Tell(new Crash());
            });

            // all previous SCOPED dependencies should be disposed
            AwaitAssert(() =>
            {
                deps1.Dependencies.Where(x => !(x is AkkaDiFixture.ISingletonDependency)).All(x => x.Disposed).Should().BeTrue();
            });

            // singletons should not be disposed
            deps1.Dependencies.Where(x => (x is AkkaDiFixture.ISingletonDependency)).All(x => x.Disposed).Should().BeFalse();

            // actor should restart with totally new dependencies (minus the singleton)
            scoped1.Tell(new FetchDependencies());
            var deps2 = ExpectMsg<CurrentDependencies>();
            deps2.Dependencies.All(x => x.Disposed).Should().BeFalse();
            var deps1Single = deps1.Dependencies.Single(x => (x is AkkaDiFixture.ISingletonDependency));
            var deps2Single = deps2.Dependencies.Single(x => (x is AkkaDiFixture.ISingletonDependency));

            deps1Single.Should().Be(deps2Single);
        }

        [Fact(DisplayName =
            "DI: actors who receive a non-DI'd dependencies should be started correctly")]
        public void ActorsWithNonDiDependenciesShouldStart()
        {
            // <CreateNonDiActor>
            var spExtension = ServiceProvider.For(Sys);
            var arg1 = "foo";
            var arg2 = "bar";
            var props = spExtension.Props<NonDiArgsActor>(arg1, arg2);

            // create a scoped actor using the props from Akka.DependencyInjection
            var scoped1 = Sys.ActorOf(props, "scoped1");
            // </CreateNonDiActor>
            scoped1.Tell(new FetchDependencies());
            var deps1 = ExpectMsg<CurrentDependencies>();
            deps1.Dependencies.All(x => x.Disposed).Should().BeFalse();

            // validate that non-DI'd arguments were passed to actor constructor arguments correctly
            scoped1.Tell("fetch");
            ExpectMsg<string>().Should().Be(arg1);
            ExpectMsg<string>().Should().Be(arg2);

            // crash + restart it
            EventFilter.Exception<ApplicationException>().ExpectOne(() =>
            {
                scoped1.Tell(new Crash());
            });

            AwaitAssert(() =>
            {
                // all previous SCOPED dependencies should eventually be disposed
                deps1.Dependencies.Where(x => !(x is AkkaDiFixture.ISingletonDependency)).All(x => x.Disposed).Should().BeTrue();
            }, 
                duration: TimeSpan.FromMilliseconds(300),
                interval: TimeSpan.FromMilliseconds(100));

            // singletons should not be disposed
            deps1.Dependencies.Where(x => (x is AkkaDiFixture.ISingletonDependency)).All(x => x.Disposed).Should().BeFalse();

            // actor should restart with totally new dependencies (minus the singleton)
            scoped1.Tell(new FetchDependencies());
            var deps2 = ExpectMsg<CurrentDependencies>();
            deps2.Dependencies.All(x => x.Disposed).Should().BeFalse();
            var deps1Single = deps1.Dependencies.Single(x => (x is AkkaDiFixture.ISingletonDependency));
            var deps2Single = deps2.Dependencies.Single(x => (x is AkkaDiFixture.ISingletonDependency));

            deps1Single.Should().Be(deps2Single);
        }

        [Fact(DisplayName = "Props created via the ServiceProvider should support the standard Props copying methods")]
        public void ServiceProvider_Props_should_support_copying()
        {
            // <CreateNonDiActor>
            var spExtension = ServiceProvider.For(Sys);
            var arg1 = "foo";
            var arg2 = "bar";
            var props = spExtension.Props<NonDiArgsActor>(arg1, arg2).WithRouter(new RoundRobinPool(10).WithSupervisorStrategy(new OneForOneStrategy(
                ex =>
                {
                    TestActor.Tell(ex);
                    return Directive.Restart;
                })));

            // create a scoped round robin pool using the props from Akka.DependencyInjection
            var scoped1 = Sys.ActorOf(props, "scoped1");

            // validate that non-DI'd arguments were passed to actor constructor arguments correctly
            scoped1.Tell("fetch");
            ExpectMsg<string>().Should().Be(arg1);
            ExpectMsg<string>().Should().Be(arg2);

            // validate that this is a router
            scoped1.Tell(GetRoutees.Instance);
            ExpectMsg<Routees>().Members.Count().Should().Be(10);

            // validate that the router's supervision strategy has been enforced
            scoped1.Tell(new Crash());
            ExpectMsg<ApplicationException>();
        }

        public class Crash { }

        public class FetchDependencies { }

        public class CurrentDependencies
        {
            public CurrentDependencies(AkkaDiFixture.IDependency[] dependencies)
            {
                Dependencies = dependencies;
            }

            public AkkaDiFixture.IDependency[] Dependencies { get; }
        }

        public class ScopedActor : ReceiveActor
        {
            private readonly IServiceScope _scope;
            private AkkaDiFixture.ITransientDependency _transient;
            private AkkaDiFixture.IScopedDependency _scoped;

            public ScopedActor(IServiceProvider sp)
            {
                _scope = sp.CreateScope();

                Receive<FetchDependencies>(_ =>
                {
                    Sender.Tell(new CurrentDependencies(new AkkaDiFixture.IDependency[] { _transient, _scoped }));
                });

                Receive<Crash>(_ => throw new ApplicationException("crash"));
            }

            protected override void PreStart()
            {
                _scoped = _scope.ServiceProvider.GetService<AkkaDiFixture.IScopedDependency>();
                _transient = _scope.ServiceProvider.GetRequiredService<AkkaDiFixture.ITransientDependency>();
            }

            protected override void PostStop()
            {
                _scope.Dispose();
            }
        }

        // <MixedActor>
        public class MixedActor : ReceiveActor
        {
            private readonly AkkaDiFixture.ISingletonDependency _singleton;
            private readonly IServiceScope _scope;
            private AkkaDiFixture.ITransientDependency _transient;
            private AkkaDiFixture.IScopedDependency _scoped;

            public MixedActor(AkkaDiFixture.ISingletonDependency singleton, IServiceProvider sp)
            {
                _singleton = singleton;
                _scope = sp.CreateScope();

                Receive<FetchDependencies>(_ =>
                {
                    Sender.Tell(new CurrentDependencies(new AkkaDiFixture.IDependency[] { _transient, _scoped, _singleton }));
                });

                Receive<Crash>(_ => throw new ApplicationException("crash"));
            }

            protected override void PreStart()
            {
                _scoped = _scope.ServiceProvider.GetService<AkkaDiFixture.IScopedDependency>();
                _transient = _scope.ServiceProvider.GetRequiredService<AkkaDiFixture.ITransientDependency>();
            }

            protected override void PostStop()
            {
                _scope.Dispose();
            }
        }
        // </MixedActor>

        // <NonDiArgsActor>
        public class NonDiArgsActor : ReceiveActor
        {
            private readonly AkkaDiFixture.ISingletonDependency _singleton;
            private readonly IServiceScope _scope;
            private AkkaDiFixture.ITransientDependency _transient;
            private AkkaDiFixture.IScopedDependency _scoped;
            private string _arg1;
            private string _arg2;

            public NonDiArgsActor(AkkaDiFixture.ISingletonDependency singleton, IServiceProvider sp, string arg1, string arg2)
            {
                _singleton = singleton;
                _scope = sp.CreateScope();
                _arg1 = arg1;
                _arg2 = arg2;

                Receive<FetchDependencies>(_ =>
                {
                    Sender.Tell(new CurrentDependencies(new AkkaDiFixture.IDependency[] { _transient, _scoped, _singleton }));
                });

                Receive<string>(str =>
                {
                    Sender.Tell(_arg1);
                    Sender.Tell(_arg2);
                });

                Receive<Crash>(_ => throw new ApplicationException("crash"));
            }

            protected override void PreStart()
            {
                _scoped = _scope.ServiceProvider.GetService<AkkaDiFixture.IScopedDependency>();
                _transient = _scope.ServiceProvider.GetRequiredService<AkkaDiFixture.ITransientDependency>();
            }

            protected override void PostStop()
            {
                _scope.Dispose();
            }
        }
        // </NonDiArgsActor>
    }


}
