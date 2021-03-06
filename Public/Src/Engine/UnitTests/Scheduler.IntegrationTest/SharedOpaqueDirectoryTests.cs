// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.Native.IO;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Tracing;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using Test.BuildXL.Executables.TestProcess;
using Test.BuildXL.Scheduler;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;
using LogEventId = BuildXL.Scheduler.Tracing.LogEventId;
using BuildXL.Processes;

namespace IntegrationTest.BuildXL.Scheduler
{
    [Trait("Category", "SharedOpaqueDirectoryTests")]
    [Feature(Features.SharedOpaqueDirectory)]
    public class SharedOpaqueDirectoryTests : SchedulerIntegrationTestBase
    {
        public SharedOpaqueDirectoryTests(ITestOutputHelper output) : base(output)
        {
            // TODO: remove when the default changes
            ((UnsafeSandboxConfiguration)(Configuration.Sandbox.UnsafeSandboxConfiguration)).IgnoreDynamicWritesOnAbsentProbes = false;
        }

        /// <summary>
        /// Creates a shared opaque directory producer & consumer and verifies their usage and caching behavior
        /// </summary>
        [Fact]
        public void SharedOpaqueDirectoryConsumptionCachingBehavior()
        {
            // Set up PipA  => sharedOpaqueDirectory => PipB
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "partialDir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            FileArtifact outputInSharedOpaque = CreateOutputFileArtifact(sharedOpaqueDir);
            FileArtifact source = CreateSourceFile();

            var pipA = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, sourceFileToRead: source, new KeyValuePair<FileArtifact, string>(outputInSharedOpaque, null));

            var builderB = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(outputInSharedOpaque, doNotInfer:true),
                Operation.WriteFile(CreateOutputFileArtifact())
            });

            builderB.AddInputDirectory(pipA.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath));
            var pipB = SchedulePipBuilder(builderB);

            // B should be able to consume the file in the opaque directory. Second build should have both cached
            RunScheduler().AssertCacheMiss(pipA.Process.PipId, pipB.Process.PipId);
            RunScheduler().AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId);

            // Make sure we can replay the file in the opaque directory
            File.Delete(ArtifactToString(outputInSharedOpaque));
            RunScheduler().AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId);
            XAssert.IsTrue(File.Exists(ArtifactToString(outputInSharedOpaque)));

            // Modify the input and make sure both are rerun
            File.WriteAllText(ArtifactToString(source), "New content");
            RunScheduler().AssertCacheMiss(pipA.Process.PipId, pipB.Process.PipId);
            RunScheduler().AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId);
        }

        [Theory]
        [InlineData(true, Operation.Type.Probe)]
        [InlineData(true, Operation.Type.ReadFile)]
        [InlineData(false, Operation.Type.Probe)]
        [InlineData(false, Operation.Type.ReadFile)]
        public void SharedOpaqueDirectoryBehaviorUnderLazyMaterialization(bool enableLazyOutputMaterialization, Operation.Type readType)
        {
            XAssert.IsTrue(readType == Operation.Type.Probe || readType == Operation.Type.ReadFile);

            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            DirectoryArtifact sharedOpaqueDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(sharedOpaqueDirPath);
            var outputInSharedOpaqueDir = CreateOutputFileArtifact(root: sharedOpaqueDir, prefix: "sod-file");

            FileArtifact source = CreateSourceFile();

            // pipA: CreateDir('sod'), WriteFile('sod/sod-file')
            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.CreateDir(sharedOpaqueDirArtifact, doNotInfer: true),
                Operation.WriteFile(outputInSharedOpaqueDir, doNotInfer: true)
            });
            builderA.ToolDescription = StringId.Create(Context.StringTable, "PipA-Producer");
            builderA.AddOutputDirectory(sharedOpaqueDirArtifact, SealDirectoryKind.SharedOpaque);
            var pipA = SchedulePipBuilder(builderA);

            // pipB: Read/Probe('sod/sod-file'), WriteFile('pip-b-out')
            var pipBOutFile = CreateOutputFileArtifact(prefix: "pip-b-out");
            var builderB = CreatePipBuilder(new Operation[]
            {
                readType == Operation.Type.Probe
                    ? Operation.Probe(outputInSharedOpaqueDir, doNotInfer: true)
                    : Operation.ReadFile(outputInSharedOpaqueDir, doNotInfer: true),
                Operation.WriteFile(pipBOutFile)
            });
            builderB.AddInputDirectory(pipA.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirArtifact.Path));
            builderB.ToolDescription = StringId.Create(Context.StringTable, "PipB-Consumer");
            var pipB = SchedulePipBuilder(builderB);

            // set filter output='*/pip-b-out'
            Configuration.Schedule.EnableLazyOutputMaterialization = enableLazyOutputMaterialization;
            Configuration.Filter = $"output='*{Path.DirectorySeparatorChar}{pipBOutFile.Path.GetName(Context.PathTable).ToString(Context.StringTable)}'";

            // run1 -> cache misses
            RunScheduler().AssertCacheMiss(pipA.Process.PipId, pipB.Process.PipId);

            // scrub shared opaque directory content (which would automatically happen in full BuildXL)
            var sodFilePath = outputInSharedOpaqueDir.Path.ToString(Context.PathTable);
            FileUtilities.DeleteDirectoryContents(sharedOpaqueDir, deleteRootDirectory: false);
            XAssert.IsFalse(File.Exists(sodFilePath), "expected to have scrubbed file {0}", sodFilePath);

            // run2 -> cache hits
            RunScheduler().AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId);
        }

        [Fact]
        public void SharedOpaqueDirectoryConsumptionCachingBehaviorWithUndeclaredReadMode()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "partialDir");
            DirectoryArtifact sharedOpaqueRoot = CreateOutputDirectoryArtifact(sharedOpaqueDir);
            AbsolutePath nestedDirUnderSharedOpaque = sharedOpaqueRoot.Path.Combine(Context.PathTable, "nested");
            FileArtifact outputInSharedOpaque = CreateOutputFileArtifact(nestedDirUnderSharedOpaque);

            // Create a pip that writes a file in a nested directory under a shared opaque, with allowed undeclared
            // reads enabled
            var builder = CreatePipBuilder(new Operation[]
                {
                    Operation.CreateDir(DirectoryArtifact.CreateWithZeroPartialSealId(nestedDirUnderSharedOpaque), doNotInfer: true),
                    Operation.WriteFile(outputInSharedOpaque, doNotInfer: true),
                });
            builder.AddOutputDirectory(sharedOpaqueRoot, SealDirectoryKind.SharedOpaque);
            builder.Options |= Process.Options.AllowUndeclaredSourceReads;

            // First run should be a miss, second one a hit
            var processWithOutputs = SchedulePipBuilder(builder);
            RunScheduler().AssertCacheMiss(processWithOutputs.Process.PipId);
            RunScheduler().AssertCacheHit(processWithOutputs.Process.PipId);

            // Assert the output was produced. Then delete it to mimic the regular scrubbing behavior.
            XAssert.IsTrue(File.Exists(outputInSharedOpaque.Path.ToString(Context.PathTable)));
            File.Delete(outputInSharedOpaque.Path.ToString(Context.PathTable));

            // Run the pip again. It should still be a hit. This makes sure that
            // accesses related to outputs don't end up as part of the fingerprint. In this
            // particular case, we should be skipping an access for the directory creation and 
            // another one for the file creation
            RunScheduler().AssertCacheHit(processWithOutputs.Process.PipId);
        }

        [Theory]
        [InlineData(PreserveOutputsMode.Enabled)]
        [InlineData(PreserveOutputsMode.Reset)]
        public void WarningIsDisplayedWhenPreserveOutputsIsOnAndThereAreSharedOpaques(PreserveOutputsMode enabledMode)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.PreserveOutputs = enabledMode;
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "partialDir");

            var pipA = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, sourceFileToRead: FileArtifact.Invalid, CreateOutputFileArtifact(sharedOpaqueDir));

            RunScheduler().AssertSuccess();
            AssertWarningEventLogged(EventId.PreserveOutputsDoNotApplyToSharedOpaques);
        }

        /// <summary>
        /// Creates a pip that writes a directory on a shared opaque dir and makes sure it is *not* cached
        /// </summary>
        [Fact]
        public void SharedOpaqueDirectoryWriting()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "partialDir");
            var sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            var sharedOpaqueDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(sharedOpaqueDirPath);
            // We output one file and one directory
            var outputFile = CreateOutputFileArtifact(sharedOpaqueDir);
            var outputDirectory = CreateOutputFileArtifact(sharedOpaqueDir);

            var builderA = CreatePipBuilder(new Operation[]
                                        {
                                            Operation.CreateDir(outputDirectory, doNotInfer: true),
                                            Operation.WriteFile(outputFile, doNotInfer: true)
                                        });
            builderA.AddOutputDirectory(sharedOpaqueDirArtifact, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builderA);

            RunScheduler().AssertSuccess();

            // Remove the whole directory
            FileUtilities.DeleteDirectoryContents(ArtifactToString(sharedOpaqueDirArtifact));

            // Replay from the cache
            RunScheduler().AssertSuccess(); 

            // The output file should exist, the directory shouldn't
            XAssert.IsTrue(File.Exists(ArtifactToString(outputFile)));
            XAssert.IsTrue(!File.Exists(ArtifactToString(outputDirectory)));
        }

        [Fact]
        public void SharedOpaqueDirectoryContentIsCorrectlyCachedOnDeletion()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            var sharedOpaqueDirectoryArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(sharedOpaqueDirPath);

            var outputArtifact = CreateOutputFileArtifact(sharedOpaqueDir);
            var outputArtifactLaterDeleted = CreateOutputFileArtifact(sharedOpaqueDir);

            var builder = CreatePipBuilder(new List<Operation>
                             {
                                 Operation.WriteFile(outputArtifact, doNotInfer: true),
                                 Operation.WriteFile(outputArtifactLaterDeleted, doNotInfer: true),
                                 Operation.DeleteFile(outputArtifactLaterDeleted, doNotInfer: true)
                             });
            builder.AddOutputDirectory(sharedOpaqueDirectoryArtifact, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builder);

            RunScheduler().AssertSuccess();

            File.Delete(ArtifactToString(outputArtifact));
            File.Delete(ArtifactToString(outputArtifactLaterDeleted));

            // Replay from cache. We should only get the file that existed when the pip finished
            RunScheduler().AssertSuccess();
            XAssert.IsTrue(File.Exists(ArtifactToString(outputArtifact)));
            XAssert.IsFalse(File.Exists(ArtifactToString(outputArtifactLaterDeleted)));
        }

        /// <summary>
        /// Consumers can only read files in an opaque directoy that were produced by its declared producers
        /// </summary>
        [Fact]
        public void ConsumersCanOnlyReadFromProducersInSharedOpaque()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            // PipA produces outputArtifactA in a shared opaque directory
            FileArtifact outputArtifactA = CreateOutputFileArtifact(sharedOpaqueDir);
            // Dummy output, just to force an order between pips and avoid races
            FileArtifact dummyOutputA = CreateOutputFileArtifact();
            var pipA = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: dummyOutputA, CreateSourceFile(), new KeyValuePair<FileArtifact, string>(outputArtifactA, null));

            // PipB produces outputArtifactB in the same shared opaque directory
            FileArtifact outputArtifactB = CreateOutputFileArtifact(sharedOpaqueDir);
            CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, CreateSourceFile(), new KeyValuePair<FileArtifact, string>(outputArtifactB, null));

            // PipC reads outputArtifactA, declaring a dependency on PipA's shared opaque
            var dummyOutputC = CreateOutputFileArtifact();
            var builderC = CreatePipBuilder(new Operation[]
            {
                Operation.ReadFile(outputArtifactA, doNotInfer: true),
                Operation.WriteFile(dummyOutputC)
            });
            builderC.AddInputDirectory(pipA.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath));
            SchedulePipBuilder(builderC);

            RunScheduler().AssertSuccess();

            ResetPipGraphBuilder();

            // Re-create pipA and pipB as before
            pipA = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: dummyOutputA, CreateSourceFile(), new KeyValuePair<FileArtifact, string>(outputArtifactA, null));
            var pipB = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, CreateSourceFile(), new KeyValuePair<FileArtifact, string>(outputArtifactB, null));

            // PipD reads outputArtifactA declaring a dependency on PipB's shared opaque - this should be disallowed
            var builderD = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.ReadFile(outputArtifactA, doNotInfer: true),
                                                       Operation.ReadFile(pipA.ProcessOutputs.GetOutputFile(dummyOutputA)), // just force a dependency, and infer it, to avoid races
                                                       Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                                   });
            builderD.AddInputDirectory(pipB.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath));
            SchedulePipBuilder(builderD);

            IgnoreWarnings();
            RunScheduler().AssertFailure();

            AssertVerboseEventLogged(EventId.PipProcessDisallowedFileAccess);
            AssertErrorEventLogged(EventId.FileMonitoringError);
        }

        [Fact]
        public void DirectoryDoubleWriteIsAllowedUnderASharedOpaque()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            // PipA writes outputArtifactA in a shared opaque directory 
            FileArtifact directory = CreateOutputFileArtifact(sharedOpaqueDir);
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.CreateDir(directory, doNotInfer: true),
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builderA);

            // PipB writes the same directory
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.CreateDir(directory, doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builderB);

            IgnoreWarnings();
            RunScheduler().AssertSuccess();
        }

        [Fact]
        public void TwoPipsProducingTheSameFileDynamicallyAndStaticallyUnderASharedOpaqueDirectoryIsBlocked()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            // PipA writes outputArtifactA in a shared opaque directory 
            FileArtifact outputArtifactA = CreateOutputFileArtifact(sharedOpaqueDir);
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resA = SchedulePipBuilder(builderA);

            // PipB produces the same artifact outputArtifactA but statically 
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA),
                                                   });
            
            // Let's make B depend on A so we avoid potential file locks on the double write
            builderB.AddInputDirectory(resA.Process.DirectoryOutputs.Single());

            SchedulePipBuilder(builderB);

            IgnoreWarnings();
            RunScheduler().AssertFailure();
            
            // We are expecting a double write
            AssertVerboseEventLogged(LogEventId.DependencyViolationDoubleWrite);
            AssertErrorEventLogged(EventId.FileMonitoringError);

            // We might get a put content failed event if the file was being written by one pip while being cached by the second
            AllowErrorEventMaybeLogged(EventId.StorageCachePutContentFailed);
            AllowErrorEventMaybeLogged(EventId.ProcessingPipOutputFileFailed);
        }

        [Fact]
        public void TwoPipsProducingTheSameFileDynamicallyUnderASharedOpaqueDirectoryIsBlocked()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            // PipA writes outputArtifactA in a shared opaque directory
            FileArtifact outputArtifactA = CreateOutputFileArtifact(sharedOpaqueDir);
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resA = SchedulePipBuilder(builderA);

            // PipB writes outputArtifactA in a shared opaque directory
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            // Let's make B depend on A so we avoid potential file locks on the double write
            builderB.AddInputDirectory(resA.Process.DirectoryOutputs.Single());
            SchedulePipBuilder(builderB);


            IgnoreWarnings();
            RunScheduler().AssertFailure();

            // We are expecting a double write
            AssertVerboseEventLogged(LogEventId.DependencyViolationDoubleWrite);
            AssertErrorEventLogged(EventId.FileMonitoringError);

            // We might get a put content failed event if the file was being written by one pip while being cached by the second
            AllowErrorEventMaybeLogged(EventId.StorageCachePutContentFailed);
            AllowErrorEventMaybeLogged(EventId.ProcessingPipOutputFileFailed);
        }

        [Fact]
        public void SharedOpaqueFileWriteInsideTempDirectory()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            var tempDirUnderSharedPath = CreateUniqueDirectory(sharedOpaqueDirPath);

            // PipA writes outputArtifactA in a shared opaque directory
            FileArtifact outputArtifactA = CreateOutputFileArtifact(tempDirUnderSharedPath);
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resA = SchedulePipBuilder(builderA);


            var sharedOpaqueDirB = Path.Combine(ObjectRoot, "sharedopaquedirB");
            AbsolutePath sharedOpaqueDirPathB = AbsolutePath.Create(Context.PathTable, sharedOpaqueDirB);

            // PipB writes outputArtifactA in a shared opaque directory
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.ReadFile(outputArtifactA, doNotInfer: true),
                                                   });

            builderB.TempDirectory = tempDirUnderSharedPath;

            builderB.AddOutputDirectory(sharedOpaqueDirPathB, SealDirectoryKind.SharedOpaque);

                // Let's make B depend on A so the write happens before setting the temp directory
            builderB.AddInputDirectory(resA.Process.DirectoryOutputs.Single());
            SchedulePipBuilder(builderB);


            IgnoreWarnings();
            RunScheduler().AssertFailure();

            AssertVerboseEventLogged(LogEventId.DependencyViolationSharedOpaqueWriteInTempDirectory);
            AssertErrorEventLogged(EventId.FileMonitoringError);

            // We might get a put content failed event if the file was being written by one pip while being cached by the second
            AllowErrorEventMaybeLogged(EventId.ProcessingPipOutputFileFailed);
        }

        [Fact]
        public void TwoPipsProducingTheSameFileDynamicallyUnderASharedOpaqueDirectoryWhenViolationsAreWarningsDoNotCrashBuildXL()
        {
            ((UnsafeSandboxConfiguration)Configuration.Sandbox.UnsafeSandboxConfiguration).UnexpectedFileAccessesAreErrors = false;

            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            // PipA writes outputArtifactA in a shared opaque directory
            FileArtifact outputArtifactA = CreateOutputFileArtifact(sharedOpaqueDir);
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resA = SchedulePipBuilder(builderA);

            // PipB writes outputArtifactA in a shared opaque directory
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            // Let's make B depend on A so we avoid potential file locks on the double write
            builderB.AddInputDirectory(resA.Process.DirectoryOutputs.Single());
            SchedulePipBuilder(builderB);

            RunScheduler().AssertSuccess();

            // We are expecting a double write as a verbose message.
            AssertVerboseEventLogged(LogEventId.DependencyViolationDoubleWrite);
            AssertWarningEventLogged(EventId.FileMonitoringWarning);

            // We inform about a mismatch in the file content (due to the ignored double write)
            AssertVerboseEventLogged(EventId.FileArtifactContentMismatch);

            // Verify the process not stored to cache event is raised
            AssertWarningEventLogged(EventId.ProcessNotStoredToCacheDueToFileMonitoringViolations);
        }

        [Fact]
        public void UntrackedPathsUnderSharedOpaqueAreHonored()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            var nested = Path.Combine(sharedOpaqueDir, "nested");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            AbsolutePath nestedPath = AbsolutePath.Create(Context.PathTable, nested);

            // PipA writes outputArtifactA in a shared opaque directory 
            // and an untracked file underneath 
            FileArtifact outputArtifactA = CreateOutputFileArtifact(sharedOpaqueDir);
            FileArtifact untrackedArtifact = CreateOutputFileArtifact(nested);

            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.CreateDir(DirectoryArtifact.CreateWithZeroPartialSealId(nestedPath), doNotInfer: true),
                                                       Operation.WriteFile(outputArtifactA, doNotInfer: true),
                                                       Operation.WriteFile(untrackedArtifact, doNotInfer: true),
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            builderA.AddUntrackedDirectoryScope(untrackedArtifact.Path.GetParent(Context.PathTable));
            SchedulePipBuilder(builderA);

            RunScheduler().AssertSuccess();
        }

        [Fact]
        public void RewritingSourceFilesUnderSharedOpaqueIsNotAllowed()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            // Let's write a file that will serve as a source file for the pip below
            var sourceFile = CreateSourceFile(sharedOpaqueDir);

            // PipA reads from the source file. That means the source file becomes known to the build
            var dummyOutput = CreateOutputFileArtifact();
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.ReadFile(sourceFile),
                                                       Operation.WriteFile(dummyOutput),
                                                   });
            SchedulePipBuilder(builderA);

            // PipB writes to the source file as part of a shared opaque
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFile(sourceFile, doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            // We don't actually need this dependency, but this forces pipB to run after pipA, avoiding write locks on the source file
            builderB.AddInputFile(dummyOutput);
            SchedulePipBuilder(builderB);

            IgnoreWarnings();
            RunScheduler().AssertFailure();
            
            // We are expecting a file monitor violation
            AssertErrorEventLogged(EventId.FileMonitoringError);
        }

        [Fact]
        public void WritingInASourceSealNestedInAShardOpaqueIsNotAllowed()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            var nestedSourceSeal = Path.Combine(sharedOpaqueDir, "nestedSourceSeal");
            AbsolutePath nestedSourceSealPath = AbsolutePath.Create(Context.PathTable, nestedSourceSeal);
            FileUtilities.CreateDirectory(nestedSourceSeal);

            FileArtifact outputUnderSharedOpaqueAndSourceSealed = CreateOutputFileArtifact(nestedSourceSeal);
            PipConstructionHelper.SealDirectorySource(nestedSourceSealPath);

            // PipA writes under the shared opaque, but also under the source sealed underneath. This shouldn't be allowed
            var builderA = CreatePipBuilder(new []
                                                   {
                                                       Operation.WriteFile(outputUnderSharedOpaqueAndSourceSealed, doNotInfer: true),
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builderA);

            IgnoreWarnings();
            RunScheduler().AssertFailure();

            // We are expecting a file monitor violation
            AssertErrorEventLogged(EventId.FileMonitoringError);
        }

        [Fact]
        public void WritingInTheConeOfAPartiallySealedDirectoryIsAllowed()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);

            var partialDir = Path.Combine(sharedOpaqueDir, "partialDir");
            AbsolutePath partialDirPath = AbsolutePath.Create(Context.PathTable, partialDir);
            FileUtilities.CreateDirectory(partialDir);
            FileArtifact outputUnderSharedOpaqueAndPartialSealed = CreateOutputFileArtifact(partialDir);

            // PipA writes under the shared opaque, but also under the partial sealed underneath, as an explicitly defined output 
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFile(outputUnderSharedOpaqueAndPartialSealed),
                                                   });
            SchedulePipBuilder(builderA);

            // We create a partial seal directory containing the written file
            var partialDirectory = PipConstructionHelper.SealDirectoryPartial(
                partialDirPath,
                new[] {outputUnderSharedOpaqueAndPartialSealed});

            // PipB writes under the partial sealed as well, and takes a dependency on the partial seal (this last read is not really needed, but
            // it increases the chances of something going wrong)
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.ReadFile(outputUnderSharedOpaqueAndPartialSealed, doNotInfer: true),
                                                       Operation.WriteFile(CreateOutputFileArtifact(partialDir), doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            builderB.AddInputDirectory(partialDirectory);
            SchedulePipBuilder(builderB);

            IgnoreWarnings();
            RunScheduler().AssertSuccess();
        }

        [Fact]
        public void TwoPipsProducingTheSameFileDynamicallyUnderASharedOpaqueDirectoryIsBlockedEvenWhenRunFromCache()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            FileArtifact outputArtifactB = CreateOutputFileArtifact();

            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFile(outputArtifactB, "CONTENT")
                                                   });
            var resA = SchedulePipBuilder(builderA);

            // PipB writes outputArtifactA into a shared opaque directory sharedopaquedir.
            FileArtifact outputArtifactA = CreateOutputFileArtifact(sharedOpaqueDir);

            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.ReadFile(outputArtifactB),
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resB = SchedulePipBuilder(builderB);
            
            IgnoreWarnings();
            RunScheduler().AssertSuccess().AssertCacheMiss(resA.Process.PipId, resB.Process.PipId);

            ResetPipGraphBuilder();

            // PipA now writes outputArtifactA into sharedopaquedir.
            builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                       Operation.WriteFile(outputArtifactB, "CONTENT")
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builderA);

            // Pip B should run from cache, but would fail because of double writes.
            builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.ReadFile(outputArtifactB),
                                                       Operation.WriteFileWithRetries(outputArtifactA, doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builderB);

            IgnoreWarnings();
            RunScheduler().AssertFailure();

            // We are expecting a double write
            AssertVerboseEventLogged(LogEventId.DependencyViolationDoubleWrite);
            AssertErrorEventLogged(EventId.FileMonitoringError);
        }

        [Fact]
        public void AbsentFileProbeFollowedByDynamicWriteIsBlocked()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            FileArtifact absentFile = CreateOutputFileArtifact(sharedOpaqueDir);

            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.Probe(absentFile, doNotInfer: true),
                                                       Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                                   });
            var resA = SchedulePipBuilder(builderA);

            // PipB writes absentFile into a shared opaque directory sharedopaquedir.
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                       Operation.ReadFile(resA.ProcessOutputs.GetOutputFiles().First()), // force a dependency
                                                       Operation.WriteFile(absentFile, doNotInfer: true),
                                                   });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resB = SchedulePipBuilder(builderB);

            RunScheduler().AssertFailure();

            // We are expecting a write after an absent path probe
            AssertVerboseEventLogged(LogEventId.DependencyViolationWriteOnAbsentPathProbe);
            AssertVerboseEventLogged(LogEventId.AbsentPathProbeInsideUndeclaredOpaqueDirectory);
            AssertErrorEventLogged(EventId.FileMonitoringError);
        }

        [Fact]
        public void DynamicWriteFollowedByAbsentPathFileProbeIsBlocked()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            FileArtifact absentFile = CreateOutputFileArtifact(sharedOpaqueDir);

            // Write and delete 'absentFile' under a shared opaque.
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                        Operation.WriteFile(absentFile, doNotInfer: true),
                                                        Operation.DeleteFile(absentFile, doNotInfer: true),
                                                        Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resA = SchedulePipBuilder(builderA);

            // Probe the absent file. Even though it was deleted by the previous pip, we should get a absent file probe violation
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                        Operation.ReadFile(resA.ProcessOutputs.GetOutputFiles().First()), // force a dependency
                                                        Operation.Probe(absentFile, doNotInfer: true),
                                                        Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                                   });
            var resB = SchedulePipBuilder(builderB);

            RunScheduler().AssertFailure();

            // We are expecting a write on an absent path probe
            AssertVerboseEventLogged(LogEventId.DependencyViolationWriteOnAbsentPathProbe);
            AssertErrorEventLogged(EventId.FileMonitoringError);
        }

        [Fact]
        public void DynamicTemporaryFileWriteFollowedByAbsentPathFileProbeIsAllowedForDependencies()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            FileArtifact absentFile = CreateOutputFileArtifact(sharedOpaqueDir);

            // Write and delete 'absentFile' under a shared opaque.
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                        Operation.WriteFile(absentFile, doNotInfer: true),
                                                        Operation.DeleteFile(absentFile, doNotInfer: true),
                                                        Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var resA = SchedulePipBuilder(builderA);

            // Probe the absent file. Even though there was a write access to that path, we do not block probes to that path
            // because we take a dependency on the directory that produced that path (the probe is guaranteed to always be absent).
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                        Operation.Probe(absentFile, doNotInfer: true),
                                                        Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                                   });
            builderB.AddInputDirectory(resA.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath));
            var resB = SchedulePipBuilder(builderB);

            RunScheduler().AssertSuccess();
        }

        [Fact]
        public void AbsentFileProbeFollowedByDynamicWriteIsBlockedOnProbeCacheReplay()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            FileArtifact absentFileUnderSharedOpaque = CreateOutputFileArtifact(sharedOpaqueDir);
            var filePipA = CreateOutputFileArtifact();

            // PipA probes absentFileUnderSharedOpaque under an opaque directory.
            var builderA = CreatePipBuilder(new Operation[]
                {
                    Operation.Probe(absentFileUnderSharedOpaque, doNotInfer: true),
                    Operation.WriteFile(filePipA) // dummy output
                });
            var pipA = SchedulePipBuilder(builderA);

            // PipB writes absentFileUnderSharedOpaque into a shared opaque directory sharedopaquedir.
            var builderB = CreatePipBuilder(new Operation[] 
                {
                    Operation.ReadFile(pipA.ProcessOutputs.GetOutputFiles().First()), // force a dependency
                    Operation.WriteFile(absentFileUnderSharedOpaque, doNotInfer: true),
                });
            builderB.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var pipB = SchedulePipBuilder(builderB);
            
            // run once to cache pipA
            RunScheduler().AssertFailure();
            
            // scrub the outputs
            File.Delete(filePipA.Path.ToString(Context.PathTable));
            FileUtilities.DeleteDirectoryContents(sharedOpaqueDir, deleteRootDirectory: true);

            // run second time -- PipA should come from cache, PipB should run but still hit the same violation
            var result = RunScheduler().AssertFailure();
            result.AssertCacheHitWithoutAssertingSuccess(pipA.Process.PipId);

            // We are expecting a write after an absent path probe (one message per run)
            AssertVerboseEventLogged(LogEventId.DependencyViolationWriteOnAbsentPathProbe, 2);
            AssertErrorEventLogged(EventId.FileMonitoringError, 2);
        }

        [Fact]
        public void DynamicWriteFollowedByAbsentPathFileProbeIsBlockedOnWriterCacheReplay()
        {
            var sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            FileArtifact absentFileUnderSharedOpaque = CreateOutputFileArtifact(sharedOpaqueDir);
            var filePipA = CreateOutputFileArtifact();
            var filePipB = CreateOutputFileArtifact();
            
            var builderA = CreatePipBuilder(new Operation[]
                                                   {
                                                        Operation.WriteFile(absentFileUnderSharedOpaque, doNotInfer: true),
                                                        Operation.DeleteFile(absentFileUnderSharedOpaque, doNotInfer: true),
                                                        Operation.WriteFile(filePipA) // dummy output
                                                   });
            builderA.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            var pipA = SchedulePipBuilder(builderA);
            
            // Probe the absent file. Even though it was deleted by the previous pip, we should get a absent file probe violation
            var builderB = CreatePipBuilder(new Operation[]
                                                   {
                                                        Operation.ReadFile(pipA.ProcessOutputs.GetOutputFiles().First()), // force a dependency
                                                        Operation.Probe(absentFileUnderSharedOpaque, doNotInfer: true),
                                                        Operation.WriteFile(filePipB) // dummy output
                                                   });
            var pipB = SchedulePipBuilder(builderB);

            // run once to cache pipA
            RunScheduler().AssertFailure();

            // scrub the outputs
            File.Delete(filePipA.Path.ToString(Context.PathTable));
            File.Delete(filePipB.Path.ToString(Context.PathTable));
            FileUtilities.DeleteDirectoryContents(sharedOpaqueDir, deleteRootDirectory: true);

            // run second time -- PipA should come from cache, PipB should run but still hit the same violation
            var result = RunScheduler().AssertFailure();
            result.AssertCacheHitWithoutAssertingSuccess(pipA.Process.PipId);

            // We are expecting a write on an absent path probe (one message per run)
            AssertVerboseEventLogged(LogEventId.DependencyViolationWriteOnAbsentPathProbe, 2);
            AssertErrorEventLogged(EventId.FileMonitoringError, 2);
        }

        [Feature(Features.DirectoryEnumeration)]
        [Feature(Features.GraphFileSystem)]
        [Fact]
        public void EnumerateSharedOpaqueDirectory()
        {
            Configuration.Sandbox.FileSystemMode = FileSystemMode.RealAndPipGraph;

            // producerA and producerB contribute to the same shared opaque root. Consumer enumerates the root of the shared opaque.
            FileArtifact inputA = CreateSourceFile();
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            var firstFileAndOutput = new KeyValuePair<FileArtifact, string>(CreateOutputFileArtifact(sharedOpaqueDir), "1");
            var secondFileAndOutput = new KeyValuePair<FileArtifact, string>(CreateOutputFileArtifact(sharedOpaqueDir), "2");
            FileArtifact consumerOutput = CreateOutputFileArtifact();

            var producerA = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, sourceFileToRead: inputA, firstFileAndOutput);
            var producerB = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, sourceFileToRead: inputA, secondFileAndOutput);
            var consumer = CreateAndScheduleConsumingPip(consumerOutput, producerA.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath), producerB.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath));

            // Ensure the correct baseline behavior
            RunScheduler().AssertSuccess();
            RunScheduler().AssertCacheHit(producerA.Process.PipId, producerB.Process.PipId, consumer.Process.PipId);

            // Modify A's and B's input. Its output is the same so B should still be a hit
            File.WriteAllText(ArtifactToString(inputA), "asdf");
            ResetPipGraphBuilder();

            var run2Result = RunScheduler();
            run2Result.AssertCacheHit(consumer.Process.PipId);
            run2Result.AssertCacheMiss(producerA.Process.PipId, producerB.Process.PipId);

            // Now, modify A such that it produces an additional file in the opaque directory
            ResetPipGraphBuilder();
            producerA = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, sourceFileToRead: inputA, firstFileAndOutput,
                new KeyValuePair<FileArtifact, string>(CreateOutputFileArtifact(sharedOpaqueDir), "2"));
            producerB = CreateAndScheduleSharedOpaqueProducer(sharedOpaqueDir, fileToProduceStatically: FileArtifact.Invalid, sourceFileToRead: inputA, secondFileAndOutput);
            consumer = CreateAndScheduleConsumingPip(consumerOutput, producerA.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath), producerB.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirPath));

            // A is a cache miss because its command line changes. B should be a hit because nothing changed for it. The consumer should be a miss because the result of the enumeration changed.
            // This is the case even if RealAndPipGraph was selected
            var run3Result = RunScheduler();
            run3Result.AssertCacheMiss(producerA.Process.PipId);
            run3Result.AssertCacheHit(producerB.Process.PipId);
            run3Result.AssertCacheMiss(consumer.Process.PipId);
        }

        [Fact]
        public void FirstDoubleWriteWinsMakesPipUnCacheable()
        {
            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sharedopaquedir");
            AbsolutePath sharedOpaqueDirPath = 
                AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            IEnumerable<Operation> writeOperation = 
                new Operation[]
                {
                    Operation.WriteFileWithRetries(
                        CreateOutputFileArtifact(sharedOpaqueDir), 
                        doNotInfer: true),
                };

            FirstDoubleWriteWinsMakesPipUnCacheableRunner(
                writeOperation, 
                sharedOpaqueDirPath,
                originalProducerPipCacheHitExpected: false,
                doubleWriteProducerPipCacheHitExpected: false);

            ResetPipGraphBuilder();

            FirstDoubleWriteWinsMakesPipUnCacheableRunner(
                writeOperation, 
                sharedOpaqueDirPath,
                originalProducerPipCacheHitExpected: true,
                doubleWriteProducerPipCacheHitExpected: false);
        }

        private void FirstDoubleWriteWinsMakesPipUnCacheableRunner(
            IEnumerable<Operation> writeOperation, 
            AbsolutePath sharedOpaqueDirPath, 
            bool originalProducerPipCacheHitExpected,
            bool doubleWriteProducerPipCacheHitExpected)
        {
            // originalProducerPip writes an artifact in a shared opaque directory
            ProcessBuilder originalProducerPipBuilder = CreatePipBuilder(writeOperation);
            originalProducerPipBuilder.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);
            ProcessWithOutputs originalProducerPipResult = SchedulePipBuilder(originalProducerPipBuilder);

            // doubleWriteProducerPip writes the same artifact in a shared opaque directory
            ProcessBuilder doubleWriteProducerPipBuilder = CreatePipBuilder(writeOperation);
            doubleWriteProducerPipBuilder.AddOutputDirectory(sharedOpaqueDirPath, SealDirectoryKind.SharedOpaque);

            // Let's make doubleWriteProducerPip depend on originalProducerPip so we avoid potential file locks on the double write
            doubleWriteProducerPipBuilder.AddInputDirectory(originalProducerPipResult.Process.DirectoryOutputs.Single());

            // Set UnsafeFirstDoubleWriteWins
            doubleWriteProducerPipBuilder.DoubleWritePolicy |= DoubleWritePolicy.UnsafeFirstDoubleWriteWins;
            ProcessWithOutputs doubleWriteProducerPipResult = SchedulePipBuilder(doubleWriteProducerPipBuilder);

            var result = RunScheduler().AssertSuccess();

            if (originalProducerPipCacheHitExpected)
            {
                result.AssertCacheHit(originalProducerPipResult.Process.PipId);
            }
            else
            {
                result.AssertCacheMiss(originalProducerPipResult.Process.PipId);
            }

            if (doubleWriteProducerPipCacheHitExpected)
            {
                result.AssertCacheHit(doubleWriteProducerPipResult.Process.PipId);
            }
            else
            {
                result.AssertCacheMiss(doubleWriteProducerPipResult.Process.PipId);
            }

            // We are expecting a double write as a verbose message.
            AssertVerboseEventLogged(LogEventId.DependencyViolationDoubleWrite);
            AssertWarningEventLogged(EventId.FileMonitoringWarning);

            // We inform about a mismatch in the file content (due to the ignored double write)
            AssertVerboseEventLogged(EventId.FileArtifactContentMismatch);

            // Verify the process not stored to cache event is raised
            AssertWarningEventLogged(EventId.ProcessNotStoredToCacheDueToFileMonitoringViolations);
        }

        [Fact]
        public void AbsentPathProbeInUndeclaredOpaquesUnsafeModeCachedPip()
        {
            var opaqueDir = Path.Combine(ObjectRoot, "opaquedir");
            AbsolutePath opaqueDirPath = AbsolutePath.Create(Context.PathTable, opaqueDir);
            FileArtifact absentFile = CreateOutputFileArtifact(opaqueDir);

            var builderA = CreatePipBuilder(new Operation[]
                                            {
                                                Operation.Probe(absentFile, doNotInfer: true),
                                                Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                            });
            builderA.AbsentPathProbeUnderOpaquesMode = Process.AbsentPathProbeInUndeclaredOpaquesMode.Unsafe;
            var resA = SchedulePipBuilder(builderA);

            var builderB = CreatePipBuilder(new Operation[]
                                            {
                                                Operation.ReadFile(resA.ProcessOutputs.GetOutputFiles().First()), // force a dependency
                                                Operation.WriteFile(absentFile, doNotInfer: true),
                                                Operation.DeleteFile(absentFile, doNotInfer: true),
                                                Operation.WriteFile(CreateOutputFileArtifact()) // dummy output
                                            });
            builderB.AddOutputDirectory(opaqueDirPath, SealDirectoryKind.SharedOpaque);
            SchedulePipBuilder(builderB);

            // first run -- cache pipA, pipB should fail
            RunScheduler().AssertFailure();
            AssertVerboseEventLogged(LogEventId.DependencyViolationWriteOnAbsentPathProbe);
            AssertVerboseEventLogged(LogEventId.AbsentPathProbeInsideUndeclaredOpaqueDirectory);
            AssertErrorEventLogged(EventId.FileMonitoringError);

            // second run -- in Unsafe mode, the outcome of the build (pass/fail) currently depends on
            // the fact whether a pip was incrementally skipped or not: 
            var result = RunScheduler();
            if (Configuration.Schedule.IncrementalScheduling)
            {
                result.AssertSuccess();
                result.AssertCacheHit(resA.Process.PipId);
            }
            else
            {
                result.AssertFailure();
                AssertErrorEventLogged(EventId.FileMonitoringError);
                AssertVerboseEventLogged(LogEventId.DependencyViolationWriteOnAbsentPathProbe);
                AssertVerboseEventLogged(LogEventId.AbsentPathProbeInsideUndeclaredOpaqueDirectory);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ProbingDirectoryUnderSharedOpaque(bool enableLazyOutputMaterialization)
        {
            /*
             PipA:
                Declares output SharedOpaque to Dir1
                Produces files in Dir1
                Produces files in Dir1\SubDir
             PipB:
                Declares output SharedOpaque to Dir1
                Depends on Pip1
                Probes Dir1\SubDir
                Produces files in Dir1 (not overlapping with PipA output)
                
            The probe of Dir1\SubDir should be allowed.
             */

            string sharedOpaqueDir = Path.Combine(ObjectRoot, "sod");
            AbsolutePath sharedOpaqueDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueDir);
            DirectoryArtifact sharedOpaqueDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(sharedOpaqueDirPath);
            var outputInSharedOpaqueDir = CreateOutputFileArtifact(root: sharedOpaqueDir, prefix: "sod-file");

            var sharedOpaqueSubDir = Path.Combine(sharedOpaqueDir, "subDir");
            var sharedOpaqueSubDirPath = AbsolutePath.Create(Context.PathTable, sharedOpaqueSubDir);
            var sharedOpaqueSubDirArtifact = DirectoryArtifact.CreateWithZeroPartialSealId(sharedOpaqueSubDirPath);
            var outputInSharedOpaqueSubDir = CreateOutputFileArtifact(sharedOpaqueSubDir);

            // pipA: CreateDir('sod'), WriteFile('sod/sod-file'), CreateDirectory('sod/subDir'), WriteFile('sod/subDir/file')
            var builderA = CreatePipBuilder(new Operation[]
            {
                Operation.CreateDir(sharedOpaqueDirArtifact, doNotInfer: true),
                Operation.WriteFile(outputInSharedOpaqueDir, doNotInfer: true),
                Operation.CreateDir(sharedOpaqueSubDirArtifact, doNotInfer:true),
                Operation.WriteFile(outputInSharedOpaqueSubDir, doNotInfer: true),
            });
            builderA.AddOutputDirectory(sharedOpaqueDirArtifact, SealDirectoryKind.SharedOpaque);
            var pipA = SchedulePipBuilder(builderA);

            // pipB: WriteFile('pip-b-out'), Probe('sod/subDir')
            var pipBOutFile = CreateOutputFileArtifact(prefix: "pip-b-out");
            var pipBOutFileUnderOpaque = CreateOutputFileArtifact(prefix: "pip-b-out", root: sharedOpaqueDir);
            var builderB = CreatePipBuilder(new Operation[]
            {
                Operation.WriteFile(pipBOutFile),
                Operation.Probe(sharedOpaqueSubDirArtifact, doNotInfer: true),
                Operation.WriteFile(pipBOutFileUnderOpaque, doNotInfer: true),
            });
            builderB.AddInputDirectory(pipA.ProcessOutputs.GetOpaqueDirectory(sharedOpaqueDirArtifact.Path));
            builderB.AddOutputDirectory(sharedOpaqueDirArtifact, SealDirectoryKind.SharedOpaque);
            var pipB = SchedulePipBuilder(builderB);

            // set filter output='*/pip-b-out'
            Configuration.Schedule.EnableLazyOutputMaterialization = enableLazyOutputMaterialization;
            Configuration.Filter = $"output='*{Path.DirectorySeparatorChar}{pipBOutFile.Path.GetName(Context.PathTable).ToString(Context.StringTable)}'";

            // run1 -> cache misses
            RunScheduler().AssertSuccess().AssertCacheMiss(pipA.Process.PipId, pipB.Process.PipId);

            // scrub shared opaque directory content (which would automatically happen in full BuildXL)
            FileUtilities.DeleteDirectoryContents(sharedOpaqueDir, deleteRootDirectory: true);
            XAssert.IsFalse(Directory.Exists(sharedOpaqueDir));

            // run2 -> cache hits
            RunScheduler().AssertSuccess().AssertCacheHit(pipA.Process.PipId, pipB.Process.PipId);
            // if lazy materialization is on, PipA's output should not exist
            XAssert.AreEqual(enableLazyOutputMaterialization, !Directory.Exists(sharedOpaqueSubDir));
        }

        /// <summary>
        /// <see cref="SharedOpaqueOutputHelper.IsSharedOpaqueOutput"/> unconditionally returns true if 
        /// the path given to it points to a directory or a non-existent file. 
        /// 
        /// This unit test tests this behavior in presence of symbolic links.
        /// </summary>
        [TheoryIfSupported(requiresUnixBasedOperatingSystem: true)]
        [InlineData(/*isSymlink*/  true, /*isDir*/  true, /*expected*/ false)] // symlink to dir     --> no (because symlink is a file)
        [InlineData(/*isSymlink*/  true, /*isDir*/ false, /*expected*/ false)] // symlink to file    --> no
        [InlineData(/*isSymlink*/  true, /*isDir*/  null, /*expected*/ false)] // symlink to missing --> no (because symlink is a file that exists)
        [InlineData(/*isSymlink*/ false, /*isDir*/  true, /*expected*/ true)]  // dir                --> yes 
        [InlineData(/*isSymlink*/ false, /*isDir*/ false, /*expected*/ false)] // file               --> no
        [InlineData(/*isSymlink*/ false, /*isDir*/  null, /*expected*/ true)]  // missing            --> yes
        public void IsSharedOpaqueOutputTests(bool isSymlink, bool? isDir, bool expected)
        {
            AbsolutePath targetPath =
                isDir == null ? CreateUniqueSourcePath("missing") :
                isDir == true ? CreateUniqueDirectory(prefix: "dir") : 
                CreateSourceFile();

            if (isDir == true)
            {
                XAssert.IsTrue(Directory.Exists(ToString(targetPath)));
            }
            else if (isDir == false)
            {
                XAssert.IsTrue(File.Exists(ToString(targetPath)));
            }

            AbsolutePath finalPath;
            if (isSymlink)
            {
                var suffix = isDir == null ? "missing" : isDir == true ? "dir" : "file";
                AbsolutePath linkPath = CreateUniqueSourcePath(prefix: "sym-to-" + suffix);
                var maybe = FileUtilities.TryCreateSymbolicLink(ToString(linkPath), ToString(targetPath), isTargetFile: isDir != true);
                XAssert.IsTrue(maybe.Succeeded);
                finalPath = linkPath;
            }
            else
            {
                finalPath = targetPath;
            }

            XAssert.AreEqual(expected, SharedOpaqueOutputHelper.IsSharedOpaqueOutput(ToString(finalPath)));
        }

        private string ToString(AbsolutePath path) => path.ToString(Context.PathTable);
    }
}
