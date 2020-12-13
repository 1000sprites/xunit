﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using Xunit.v3;

public class TestCollectionRunnerTests
{
	[Fact]
	public static async void Messages()
	{
		var summary = new RunSummary { Total = 4, Failed = 2, Skipped = 1, Time = 21.12m };
		var messageBus = new SpyMessageBus();
		var testCase = Mocks.TestCase<ClassUnderTest>("Passing");
		var runner = TestableTestCollectionRunner.Create(messageBus, new[] { testCase }, summary);

		var result = await runner.RunAsync();

		Assert.Equal(result.Total, summary.Total);
		Assert.Equal(result.Failed, summary.Failed);
		Assert.Equal(result.Skipped, summary.Skipped);
		Assert.Equal(result.Time, summary.Time);
		Assert.False(runner.TokenSource.IsCancellationRequested);
		Assert.Collection(
			messageBus.Messages,
			msg =>
			{
				var starting = Assert.IsAssignableFrom<_TestCollectionStarting>(msg);
				Assert.Equal("assembly-id", starting.AssemblyUniqueID);
				Assert.Null(starting.TestCollectionClass);
				Assert.Equal("Mock test collection", starting.TestCollectionDisplayName);
				Assert.Equal("e59e2367912d9f03694edf120cf173056af08770263ae6215fb2cf7c9a510b0d", starting.TestCollectionUniqueID);
			},
			msg =>
			{
				var finished = Assert.IsAssignableFrom<_TestCollectionFinished>(msg);
				Assert.Equal("assembly-id", finished.AssemblyUniqueID);
				Assert.Equal(21.12m, finished.ExecutionTime);
				Assert.Equal("e59e2367912d9f03694edf120cf173056af08770263ae6215fb2cf7c9a510b0d", finished.TestCollectionUniqueID);
				Assert.Equal(2, finished.TestsFailed);
				Assert.Equal(4, finished.TestsRun);
				Assert.Equal(1, finished.TestsSkipped);
			}
		);
	}

	[Fact]
	public static async void FailureInQueueOfTestCollectionStarting_DoesNotQueueTestCollectionFinished_DoesNotRunTestClasses()
	{
		var messages = new List<_MessageSinkMessage>();
		var messageBus = Substitute.For<IMessageBus>();
		messageBus
			.QueueMessage(null!)
			.ReturnsForAnyArgs(callInfo =>
			{
				var msg = callInfo.Arg<_MessageSinkMessage>();
				messages.Add(msg);

				if (msg is _TestCollectionStarting)
					throw new InvalidOperationException();

				return true;
			});
		var runner = TestableTestCollectionRunner.Create(messageBus);

		await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync());

		var starting = Assert.Single(messages);
		Assert.IsAssignableFrom<_TestCollectionStarting>(starting);
		Assert.Empty(runner.ClassesRun);
	}

	[Fact]
	public static async void RunTestClassAsync_AggregatorIncludesPassedInExceptions()
	{
		var messageBus = new SpyMessageBus();
		var ex = new DivideByZeroException();
		var runner = TestableTestCollectionRunner.Create(messageBus, aggregatorSeedException: ex);

		await runner.RunAsync();

		Assert.Same(ex, runner.RunTestClassAsync_AggregatorResult);
		Assert.Empty(messageBus.Messages.OfType<_TestCollectionCleanupFailure>());
	}

	[Fact]
	public static async void FailureInAfterTestCollectionStarting_GivesErroredAggregatorToTestClassRunner_NoCleanupFailureMessage()
	{
		var messageBus = new SpyMessageBus();
		var runner = TestableTestCollectionRunner.Create(messageBus);
		var ex = new DivideByZeroException();
		runner.AfterTestCollectionStarting_Callback = aggregator => aggregator.Add(ex);

		await runner.RunAsync();

		Assert.Same(ex, runner.RunTestClassAsync_AggregatorResult);
		Assert.Empty(messageBus.Messages.OfType<_TestCollectionCleanupFailure>());
	}

	[Fact]
	public static async void FailureInBeforeTestCollectionFinished_ReportsCleanupFailure_DoesNotIncludeExceptionsFromAfterTestCollectionStarting()
	{
		var messageBus = new SpyMessageBus();
		var testCases = new[] { Mocks.TestCase<TestAssemblyRunnerTests.RunAsync>("Messages") };
		var runner = TestableTestCollectionRunner.Create(messageBus, testCases);
		var startingException = new DivideByZeroException();
		var finishedException = new InvalidOperationException();
		runner.AfterTestCollectionStarting_Callback = aggregator => aggregator.Add(startingException);
		runner.BeforeTestCollectionFinished_Callback = aggregator => aggregator.Add(finishedException);

		await runner.RunAsync();

		var cleanupFailure = Assert.Single(messageBus.Messages.OfType<_TestCollectionCleanupFailure>());
		Assert.Equal(typeof(InvalidOperationException).FullName, cleanupFailure.ExceptionTypes.Single());
	}

	[Fact]
	public static async void Cancellation_TestCollectionStarting_DoesNotCallExtensibilityCallbacks()
	{
		var messageBus = new SpyMessageBus(msg => !(msg is _TestCollectionStarting));
		var runner = TestableTestCollectionRunner.Create(messageBus);

		await runner.RunAsync();

		Assert.True(runner.TokenSource.IsCancellationRequested);
		Assert.False(runner.AfterTestCollectionStarting_Called);
		Assert.False(runner.BeforeTestCollectionFinished_Called);
	}

	[Fact]
	public static async void Cancellation_TestCollectionFinished_CallsExtensibilityCallbacks()
	{
		var messageBus = new SpyMessageBus(msg => !(msg is _TestCollectionFinished));
		var runner = TestableTestCollectionRunner.Create(messageBus);

		await runner.RunAsync();

		Assert.True(runner.TokenSource.IsCancellationRequested);
		Assert.True(runner.AfterTestCollectionStarting_Called);
		Assert.True(runner.BeforeTestCollectionFinished_Called);
	}

	[Fact]
	public static async void Cancellation_TestCollectionCleanupFailure_SetsCancellationToken()
	{
		var messageBus = new SpyMessageBus(msg => !(msg is _TestCollectionCleanupFailure));
		var runner = TestableTestCollectionRunner.Create(messageBus);
		runner.BeforeTestCollectionFinished_Callback = aggregator => aggregator.Add(new Exception());

		await runner.RunAsync();

		Assert.True(runner.TokenSource.IsCancellationRequested);
	}

	[Fact]
	public static async void TestsAreGroupedByCollection()
	{
		var passing1 = Mocks.TestCase<ClassUnderTest>("Passing");
		var other1 = Mocks.TestCase<ClassUnderTest>("Other");
		var passing2 = Mocks.TestCase<ClassUnderTest2>("Passing");
		var other2 = Mocks.TestCase<ClassUnderTest2>("Other");
		var runner = TestableTestCollectionRunner.Create(testCases: new[] { passing1, passing2, other2, other1 });

		await runner.RunAsync();

		Assert.Collection(
			runner.ClassesRun,
			tuple =>
			{
				Assert.Equal("TestCollectionRunnerTests+ClassUnderTest", tuple.Item1.Name);
				Assert.Collection(tuple.Item2,
					testCase => Assert.Same(passing1, testCase),
					testCase => Assert.Same(other1, testCase)
				);
			},
			tuple =>
			{
				Assert.Equal("TestCollectionRunnerTests+ClassUnderTest2", tuple.Item1.Name);
				Assert.Collection(tuple.Item2,
					testCase => Assert.Same(passing2, testCase),
					testCase => Assert.Same(other2, testCase)
				);
			}
		);
	}

	[Fact]
	public static async void SignalingCancellationStopsRunningClasses()
	{
		var passing1 = Mocks.TestCase<ClassUnderTest>("Passing");
		var passing2 = Mocks.TestCase<ClassUnderTest2>("Passing");
		var runner = TestableTestCollectionRunner.Create(testCases: new[] { passing1, passing2 }, cancelInRunTestClassAsync: true);

		await runner.RunAsync();

		var tuple = Assert.Single(runner.ClassesRun);
		Assert.Equal("TestCollectionRunnerTests+ClassUnderTest", tuple.Item1.Name);
	}

	class ClassUnderTest
	{
		[Fact]
		public void Passing() { }

		[Fact]
		public void Other() { }
	}

	class ClassUnderTest2 : ClassUnderTest { }

	class TestableTestCollectionRunner : TestCollectionRunner<_ITestCase>
	{
		readonly bool cancelInRunTestClassAsync;
		readonly RunSummary result;

		public readonly List<Tuple<IReflectionTypeInfo, IEnumerable<_ITestCase>>> ClassesRun = new List<Tuple<IReflectionTypeInfo, IEnumerable<_ITestCase>>>();
		public Action<ExceptionAggregator> AfterTestCollectionStarting_Callback = _ => { };
		public bool AfterTestCollectionStarting_Called;
		public Action<ExceptionAggregator> BeforeTestCollectionFinished_Callback = _ => { };
		public bool BeforeTestCollectionFinished_Called;
		public Exception? RunTestClassAsync_AggregatorResult;
		public readonly CancellationTokenSource TokenSource;

		TestableTestCollectionRunner(
			string testAssemblyUniqueID,
			_ITestCollection testCollection,
			IEnumerable<_ITestCase> testCases,
			IMessageBus messageBus,
			ITestCaseOrderer testCaseOrderer,
			ExceptionAggregator aggregator,
			CancellationTokenSource cancellationTokenSource,
			RunSummary result,
			bool cancelInRunTestClassAsync)
				: base(testAssemblyUniqueID, testCollection, testCases, messageBus, testCaseOrderer, aggregator, cancellationTokenSource)
		{
			TokenSource = cancellationTokenSource;

			this.result = result;
			this.cancelInRunTestClassAsync = cancelInRunTestClassAsync;
		}

		public static TestableTestCollectionRunner Create(
			IMessageBus? messageBus = null,
			_ITestCase[]? testCases = null,
			RunSummary? result = null,
			Exception? aggregatorSeedException = null,
			bool cancelInRunTestClassAsync = false)
		{
			if (testCases == null)
				testCases = new[] { Mocks.TestCase<ClassUnderTest>("Passing") };

			var aggregator = new ExceptionAggregator();
			if (aggregatorSeedException != null)
				aggregator.Add(aggregatorSeedException);

			return new TestableTestCollectionRunner(
				"assembly-id",
				testCases.First().TestMethod.TestClass.TestCollection,
				testCases,
				messageBus ?? new SpyMessageBus(),
				new MockTestCaseOrderer(),
				aggregator,
				new CancellationTokenSource(),
				result ?? new RunSummary(),
				cancelInRunTestClassAsync
			);
		}

		protected override Task AfterTestCollectionStartingAsync()
		{
			AfterTestCollectionStarting_Called = true;
			AfterTestCollectionStarting_Callback(Aggregator);
			return Task.CompletedTask;
		}

		protected override Task BeforeTestCollectionFinishedAsync()
		{
			BeforeTestCollectionFinished_Called = true;
			BeforeTestCollectionFinished_Callback(Aggregator);
			return Task.CompletedTask;
		}

		protected override Task<RunSummary> RunTestClassAsync(
			_ITestClass testClass,
			IReflectionTypeInfo @class,
			IEnumerable<_ITestCase> testCases)
		{
			if (cancelInRunTestClassAsync)
				CancellationTokenSource.Cancel();

			RunTestClassAsync_AggregatorResult = Aggregator.ToException();
			ClassesRun.Add(Tuple.Create(@class, testCases));
			return Task.FromResult(result);
		}
	}
}
