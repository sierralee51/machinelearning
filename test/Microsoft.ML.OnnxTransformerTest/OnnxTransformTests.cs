// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Model;
using Microsoft.ML.RunTests;
using Microsoft.ML.Runtime;
using Microsoft.ML.StaticPipe;
using Microsoft.ML.TestFramework.Attributes;
using Microsoft.ML.Tools;
using Microsoft.ML.Transforms.Image;
using Microsoft.ML.Transforms.StaticPipe;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.ML.Tests
{
    public class OnnxTransformTests : TestDataPipeBase
    {
        private const int inputSize = 150528;

        private class TestData
        {
            [VectorType(inputSize)]
            public float[] data_0;
        }

        private class TestDataMulti
        {
            [VectorType(5)]
            public float[] ina;

            [VectorType(5)]
            public float[] inb;
        }

        private class TestDataSize
        {
            [VectorType(2)]
            public float[] data_0;
        }

        private class TestDataXY
        {
            [VectorType(inputSize)]
            public float[] A;
        }

        private class TestDataDifferntType
        {
            [VectorType(inputSize)]
            public string[] data_0;
        }

        private class TestDataUnknownDimensions
        {
            [VectorType(3)]
            public float[] input;
        }

        class PredictionUnknownDimensions
        {
            [VectorType(1)]
            public long[] argmax { get; set; }
        }

        private float[] GetSampleArrayData()
        {
            var samplevector = new float[inputSize];
            for (int i = 0; i < inputSize; i++)
                samplevector[i] = (i / (inputSize * 1.01f));
            return samplevector;
        }

        public OnnxTransformTests(ITestOutputHelper output) : base(output)
        {
        }

        [OnnxFact]
        void TestSimpleCase()
        {
            var modelFile = "squeezenet/00000001/model.onnx";
            var samplevector = GetSampleArrayData();
            var dataView = ML.Data.LoadFromEnumerable(
                new TestData[] {
                    new TestData()
                    {
                        data_0 = samplevector
                    },
                     new TestData()
                     {
                        data_0 = samplevector
                     }
                });

            var xyData = new List<TestDataXY> { new TestDataXY() { A = new float[inputSize] } };
            var stringData = new List<TestDataDifferntType> { new TestDataDifferntType() { data_0 = new string[inputSize] } };
            var sizeData = new List<TestDataSize> { new TestDataSize() { data_0 = new float[2] } };
            var pipe = ML.Transforms.ApplyOnnxModel(new[] { "softmaxout_1" }, new[] { "data_0" }, modelFile);

            var invalidDataWrongNames = ML.Data.LoadFromEnumerable(xyData);
            var invalidDataWrongTypes = ML.Data.LoadFromEnumerable(stringData);
            var invalidDataWrongVectorSize = ML.Data.LoadFromEnumerable(sizeData);
            TestEstimatorCore(pipe, dataView, invalidInput: invalidDataWrongNames);
            TestEstimatorCore(pipe, dataView, invalidInput: invalidDataWrongTypes);

            pipe.GetOutputSchema(SchemaShape.Create(invalidDataWrongVectorSize.Schema));
            try
            {
                pipe.Fit(invalidDataWrongVectorSize);
                Assert.False(true);
            }
            catch (ArgumentOutOfRangeException) { }
            catch (InvalidOperationException) { }
        }
 
        [OnnxTheory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        void TestOldSavingAndLoading(int? gpuDeviceId, bool fallbackToCpu)
        {
            var modelFile = "squeezenet/00000001/model.onnx";
            var samplevector = GetSampleArrayData();

            var dataView = ML.Data.LoadFromEnumerable(
                new TestData[] {
                    new TestData()
                    {
                        data_0 = samplevector
                    }
                });

            var inputNames = new[] { "data_0" };
            var outputNames = new[] { "softmaxout_1" };
            var est = ML.Transforms.ApplyOnnxModel(outputNames, inputNames, modelFile, gpuDeviceId, fallbackToCpu);
            var transformer = est.Fit(dataView);
            var result = transformer.Transform(dataView);
            var resultRoles = new RoleMappedData(result);
            using (var ms = new MemoryStream())
            {
                TrainUtils.SaveModel(Env, Env.Start("saving"), ms, null, resultRoles);
                ms.Position = 0;
                var loadedView = ModelFileUtils.LoadTransforms(Env, dataView, ms);

                var sofMaxOut1Col = loadedView.Schema[outputNames[0]];

                using (var cursor = loadedView.GetRowCursor(sofMaxOut1Col))
                {
                    VBuffer<float> softMaxValue = default;
                    var softMaxGetter = cursor.GetGetter<VBuffer<float>>(sofMaxOut1Col);
                    float sum = 0f;
                    int i = 0;
                    while (cursor.MoveNext())
                    {
                        softMaxGetter(ref softMaxValue);
                        var values = softMaxValue.DenseValues();
                        foreach (var val in values)
                        {
                            sum += val;
                            if (i == 0)
                                Assert.InRange(val, 0.00004, 0.00005);
                            if (i == 1)
                                Assert.InRange(val, 0.003844, 0.003845);
                            if (i == 999)
                                Assert.InRange(val, 0.0029566, 0.0029567);
                            i++;
                        }
                    }
                    Assert.InRange(sum, 1.0, 1.00001);
                }
            }
        }

        [OnnxFact]
        public void OnnxStatic()
        {
            var modelFile = Path.Combine(Directory.GetCurrentDirectory(), "squeezenet", "00000001", "model.onnx");

            var env = new MLContext();
            var imageHeight = 224;
            var imageWidth = 224;
            var dataFile = GetDataPath("images/images.tsv");
            var imageFolder = Path.GetDirectoryName(dataFile);

            var data = TextLoaderStatic.CreateLoader(env, ctx => (
                imagePath: ctx.LoadText(0),
                name: ctx.LoadText(1)))
                .Load(dataFile);

            // Note that CamelCase column names are there to match the TF graph node names.
            var pipe = data.MakeNewEstimator()
                .Append(row => (
                    row.name,
                    data_0: row.imagePath.LoadAsImage(imageFolder).Resize(imageHeight, imageWidth).ExtractPixels(interleave: true)))
                .Append(row => (row.name, softmaxout_1: row.data_0.ApplyOnnxModel(modelFile)));

            TestEstimatorCore(pipe.AsDynamic, data.AsDynamic);

            var result = pipe.Fit(data).Transform(data).AsDynamic;
            var softmaxOutCol = result.Schema["softmaxout_1"];

            using (var cursor = result.GetRowCursor(softmaxOutCol))
            {
                var buffer = default(VBuffer<float>);
                var getter = cursor.GetGetter<VBuffer<float>>(softmaxOutCol);
                var numRows = 0;
                while (cursor.MoveNext())
                {
                    getter(ref buffer);
                    Assert.Equal(1000, buffer.Length);
                    numRows += 1;
                }
                Assert.Equal(4, numRows);
            }
        }

        [OnnxFact]
        void TestCommandLine()
        {
            var x = Maml.Main(new[] { @"showschema loader=Text{col=data_0:R4:0-150527} xf=Onnx{InputColumns={data_0} OutputColumns={softmaxout_1} model={squeezenet/00000001/model.onnx}}" });
            Assert.Equal(0, x);
        }

        [OnnxFact]
        public void OnnxModelScenario()
        {
            var modelFile = "squeezenet/00000001/model.onnx";
            var env = new ConsoleEnvironment(seed: 1);
            var samplevector = GetSampleArrayData();

            var dataView = ML.Data.LoadFromEnumerable(
                new TestData[] {
                    new TestData()
                    {
                        data_0 = samplevector
                    }
                });

            var onnx = ML.Transforms.ApplyOnnxModel("softmaxout_1", "data_0", modelFile).Fit(dataView).Transform(dataView);
            var scoreCol = onnx.Schema["softmaxout_1"];

            using (var curs = onnx.GetRowCursor(scoreCol))
            {
                var getScores = curs.GetGetter<VBuffer<float>>(scoreCol);
                var buffer = default(VBuffer<float>);
                while (curs.MoveNext())
                {
                    getScores(ref buffer);
                    Assert.Equal(1000, buffer.Length);
                }
            }
        }

        [OnnxFact]
        public void OnnxModelMultiInput()
        {
            var modelFile = Path.Combine(Directory.GetCurrentDirectory(), "twoinput", "twoinput.onnx");
            var env = new ConsoleEnvironment(seed: 1);
            var samplevector = GetSampleArrayData();

            var dataView = ML.Data.LoadFromEnumerable(
                new TestDataMulti[] {
                    new TestDataMulti()
                    {
                        ina = new float[] {1,2,3,4,5},
                        inb = new float[] {1,2,3,4,5}
                    }
                });
            var onnx = ML.Transforms.ApplyOnnxModel(new[] { "outa", "outb" }, new[] { "ina", "inb" }, modelFile).Fit(dataView).Transform(dataView);

            var outaCol = onnx.Schema["outa"];
            var outbCol = onnx.Schema["outb"];
            using (var curs = onnx.GetRowCursor(outaCol, onnx.Schema["outb"]))
            {
                var getScoresa = curs.GetGetter<VBuffer<float>>(outaCol);
                var getScoresb = curs.GetGetter<VBuffer<float>>(outbCol);
                var buffera = default(VBuffer<float>);
                var bufferb = default(VBuffer<float>);

                while (curs.MoveNext())
                {
                    getScoresa(ref buffera);
                    getScoresb(ref bufferb);
                    Assert.Equal(5, buffera.Length);
                    Assert.Equal(5, bufferb.Length);
                    Assert.Equal(0, buffera.GetValues().ToArray().Sum());
                    Assert.Equal(30, bufferb.GetValues().ToArray().Sum());
                }
            }
        }

        [OnnxFact]
        public void TestUnknownDimensions()
        {
            // model contains -1 in input and output shape dimensions
            // model: input dims = [-1, 3], output argmax dims = [-1]
            var modelFile = @"unknowndimensions/test_unknowndimensions_float.onnx";
            var mlContext = new MLContext();
            var data = new TestDataUnknownDimensions[]
                {
                    new TestDataUnknownDimensions(){input = new float[] {1.1f, 1.3f, 1.2f }},
                    new TestDataUnknownDimensions(){input = new float[] {-1.1f, -1.3f, -1.2f }},
                    new TestDataUnknownDimensions(){input = new float[] {-1.1f, -1.3f, 1.2f }},
                };
            var idv = mlContext.Data.LoadFromEnumerable(data);
            var pipeline = ML.Transforms.ApplyOnnxModel(modelFile);
            var transformedValues = pipeline.Fit(idv).Transform(idv);
            var predictions = mlContext.Data.CreateEnumerable<PredictionUnknownDimensions>(transformedValues, reuseRowObject: false).ToArray();

            Assert.Equal(1, predictions[0].argmax[0]);
            Assert.Equal(0, predictions[1].argmax[0]);
            Assert.Equal(2, predictions[2].argmax[0]);
        }

        /// <summary>
        /// This class is used in <see cref="OnnxModelInMemoryImage"/> to describe data points which will be consumed by ML.NET pipeline.
        /// </summary>
        private class ImageDataPoint
        {
            /// <summary>
            /// Height of <see cref="Image"/>.
            /// </summary>
            private const int height = 224;

            /// <summary>
            /// Width of <see cref="Image"/>.
            /// </summary>
            private const int width = 224;

            /// <summary>
            /// Image will be consumed by ONNX image multiclass classification model.
            /// </summary>
            [ImageType(height, width)]
            public Bitmap Image { get; set; }

            /// <summary>
            /// Output of ONNX model. It contains probabilities of all classes.
            /// </summary>
            [ColumnName("softmaxout_1")]
            public float[] Scores { get; set; }

            public ImageDataPoint()
            {
                Image = null;
            }

            public ImageDataPoint(Color color)
            {
                Image = new Bitmap(width, height);
                for (int i = 0; i < width; ++i)
                    for (int j = 0; j < height; ++j)
                        Image.SetPixel(i, j, color);
            }
        }

        /// <summary>
        /// Test applying ONNX transform on in-memory image.
        /// </summary>
        [OnnxFact]
        public void OnnxModelInMemoryImage()
        {
            // Path of ONNX model. It's a multiclass classifier. It consumes an input "data_0" and produces an output "softmaxout_1".
            var modelFile = "squeezenet/00000001/model.onnx";

            // Create in-memory data points. Its Image/Scores field is the input/output of the used ONNX model.
            var dataPoints = new ImageDataPoint[]
            {
                new ImageDataPoint(Color.Red),
                new ImageDataPoint(Color.Green)
            };

            // Convert training data to IDataView, the general data type used in ML.NET.
            var dataView = ML.Data.LoadFromEnumerable(dataPoints);

            // Create a ML.NET pipeline which contains two steps. First, ExtractPixle is used to convert the 224x224 image to a 3x224x224 float tensor.
            // Then the float tensor is fed into a ONNX model with an input called "data_0" and an output called "softmaxout_1". Note that "data_0" and
            // "softmaxout_1" are model input and output names stored in the used ONNX model file. Users may need to inspect their own models to
            // get the right input and output column names.
            var pipeline = ML.Transforms.ExtractPixels("data_0", "Image")                   // Map column "Image" to column "data_0"
                .Append(ML.Transforms.ApplyOnnxModel("softmaxout_1", "data_0", modelFile)); // Map column "data_0" to column "softmaxout_1"
            var model = pipeline.Fit(dataView);
            var onnx = model.Transform(dataView);

            // Convert IDataView back to IEnumerable<ImageDataPoint> so that user can inspect the output, column "softmaxout_1", of the ONNX transform.
            // Note that Column "softmaxout_1" would be stored in ImageDataPont.Scores because the added attributed [ColumnName("softmaxout_1")]
            // tells that ImageDataPont.Scores is equivalent to column "softmaxout_1".
            var transformedDataPoints = ML.Data.CreateEnumerable<ImageDataPoint>(onnx, false).ToList();

            // The scores are probabilities of all possible classes, so they should all be positive.
            foreach (var dataPoint in transformedDataPoints)
                foreach (var score in dataPoint.Scores)
                    Assert.True(score > 0);
        }
    }
}
