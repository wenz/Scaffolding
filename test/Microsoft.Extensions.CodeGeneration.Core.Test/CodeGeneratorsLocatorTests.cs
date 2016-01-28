// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using System.Collections.Generic;

namespace Microsoft.Extensions.CodeGeneration.Core.Test
{
    public class CodeGeneratorsLocatorTests
    {
        Mock<Assembly> currentAssembly;

        public CodeGeneratorsLocatorTests()
        {
            List<TypeInfo> typeList = new List<TypeInfo>();

            typeList.Add(typeof(SampleCodeGenerator).GetTypeInfo());
            typeList.Add(typeof(GeneratorDerivingFromInterface).GetTypeInfo());
            typeList.Add(typeof(InterfaceEndingWithCodeGenerator).GetTypeInfo());
            typeList.Add(typeof(AbstractClassDerivingFromInterface).GetTypeInfo());

            currentAssembly = new Mock<Assembly>();
            currentAssembly.Setup(c => c.DefinedTypes).Returns(typeList);
        }

        [Fact]
        public void CodeGeneratorsLocator_Returns_Correct_Number_Of_Generators()
        {
            //Arrange
            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockAssemblyProvider = new Mock<ICodeGeneratorAssemblyProvider>();

            mockAssemblyProvider
                .SetupGet(ap => ap.CandidateAssemblies)
                .Returns(new[] { currentAssembly.Object });

            var locator = new CodeGeneratorsLocator(mockServiceProvider.Object,
                mockAssemblyProvider.Object);

            //Act
            var generators = locator.CodeGenerators;

            //Assert
            Assert.Equal(2, generators.Count());
        }

        [Fact]
        public void CodeGeneratorsLocator_Returns_Correct_CodeGenerator_For_A_Name()
        {
            //Arrange
            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockAssemblyProvider = new Mock<ICodeGeneratorAssemblyProvider>();

            mockAssemblyProvider
                .SetupGet(ap => ap.CandidateAssemblies)
                .Returns(new[] { currentAssembly.Object });

            var locator = new CodeGeneratorsLocator(mockServiceProvider.Object,
                mockAssemblyProvider.Object);

            //Act
            var generator = locator.GetCodeGenerator("SampleCodeGenerator");

            //Assert
            Assert.NotNull(generator);
        }

        [Fact]
        public void CodeGeneratorsLocator_Throws_When_No_CodeGenerator_Found_For_A_Name()
        {
            //Arrange
            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockAssemblyProvider = new Mock<ICodeGeneratorAssemblyProvider>();

            mockAssemblyProvider
                .SetupGet(ap => ap.CandidateAssemblies)
                .Returns(new[] { currentAssembly.Object });

            var locator = new CodeGeneratorsLocator(mockServiceProvider.Object,
                mockAssemblyProvider.Object);

            //Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => locator.GetCodeGenerator("NonExistingCodeGenerator"));
            Assert.Equal("No code generators found with the name 'NonExistingCodeGenerator'", ex.Message);
        }

        
        
    }
    //This should be returned.
    public class SampleCodeGenerator
    {
    }

    //This should be returned.
    public class GeneratorDerivingFromInterface : ICodeGenerator
    {
    }

    //This should not be returned.
    public interface InterfaceEndingWithCodeGenerator
    {
    }

    //This should not be returned.
    public abstract class AbstractClassDerivingFromInterface : ICodeGenerator
    {
    }

    //This should not be returned.
    public class GenericClassCodeGenertor<T> where T : class
    {
    }
}