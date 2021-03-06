﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildXL.FrontEnd.Ninja;
using BuildXL.Utilities;

namespace BuildXL.FrontEnd.CMake.Failures
{
    internal abstract class CMakeResolverFailure : Failure
    {
        /// <inheritdoc/>
        public override BuildXLException CreateException() => new BuildXLException(Describe());

        /// <inheritdoc/>
        public override BuildXLException Throw() => throw CreateException();
    }


    internal class NinjaWorkspaceResolverInitializationFailure : CMakeResolverFailure
    {
        /// <inheritdoc/>
        public override string Describe() => "The embedded Ninja resolver wasn't successfully initialized";
    }

    internal class InnerNinjaFailure : CMakeResolverFailure
    {
        private readonly Failure m_innerFailure;
        
        /// <nodoc/>
        public InnerNinjaFailure(Failure innerFailure)
        {
            m_innerFailure = innerFailure;
        }

        /// <inheritdoc/>
        public override string Describe() => $"There was an error associated with the embedded Ninja resolver. Details: {m_innerFailure.Describe()}";
    }


    internal class CMakeGenerationError : CMakeResolverFailure
    {
        private string m_moduleName;
        private string m_buildDirectory;

        public CMakeGenerationError(string moduleName, string buildDirectory)
        {
            this.m_moduleName = moduleName;
            this.m_buildDirectory = buildDirectory;
        }

        public override string Describe() => $"There was an issue with the trying to generate the build directory {m_buildDirectory} for module {m_moduleName}. Details were logged.";
    }

}
