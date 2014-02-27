﻿using KafkaNet;
using KafkaNet.Protocol;
using Moq;
using Ninject.MockingKernel.Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace kafka_tests.Unit
{
    [TestFixture]
    public class BrokerRouterTests
    {
        private const string TestTopic = "UnitTest";
        private MoqMockingKernel _kernel;
        private Mock<IKafkaConnection> _connMock1;
        private Mock<IKafkaConnection> _connMock2;
        private Mock<IKafkaConnectionFactory> _factoryMock;
        private Mock<IPartitionSelector> _partitionSelectorMock;

        [SetUp]
        public void Setup()
        {
            _kernel = new MoqMockingKernel();

            //setup mock IKafkaConnection
            _partitionSelectorMock = _kernel.GetMock<IPartitionSelector>();
            _connMock1 = _kernel.GetMock<IKafkaConnection>();
            _connMock2 = _kernel.GetMock<IKafkaConnection>();
            _factoryMock = _kernel.GetMock<IKafkaConnectionFactory>();
            _factoryMock.Setup(x => x.Create(It.Is<Uri>(uri => uri.Port == 1), It.IsAny<int>(), It.IsAny<IKafkaLog>())).Returns(() => _connMock1.Object);
            _factoryMock.Setup(x => x.Create(It.Is<Uri>(uri => uri.Port == 2), It.IsAny<int>(), It.IsAny<IKafkaLog>())).Returns(() => _connMock2.Object);
        }

        [Test]
        public void BrokerRouterCanConstruct()
        {
            var result = new BrokerRouter(new KafkaNet.Model.KafkaOptions
            {
                KafkaServerUri = new List<Uri> { new Uri("http://localhost:1") },
                KafkaConnectionFactory = _factoryMock.Object
            });

            Assert.That(result.DefaultBrokers.Count, Is.EqualTo(1));
        }

        #region MetadataRequest Tests...
        [Test]
        public void BrokerRouteShouldCycleThroughEachBrokerUntilOneIsFound()
        {
            var router = new BrokerRouter(new KafkaNet.Model.KafkaOptions
            {
                KafkaServerUri = new List<Uri> { new Uri("http://localhost:1"), new Uri("http://localhost:2") },
                KafkaConnectionFactory = _factoryMock.Object
            });

            _connMock1.Setup(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()))
                      .Throws(new ApplicationException("some error"));

            _connMock2.Setup(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()))
                      .Returns(() => Task.Factory.StartNew(() => new List<MetadataResponse> { CreateMetaResponse() }));

            var result = router.GetTopicMetadataAsync(TestTopic).Result;

            Assert.That(result, Is.Not.Null);
            _connMock1.Verify(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()), Times.Once());
            _connMock2.Verify(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()), Times.Once());
        }

        [Test]
        public void BrokerRouteShouldThrowIfCycleCouldNotConnectToAnyServer()
        {
            var router = new BrokerRouter(new KafkaNet.Model.KafkaOptions
            {
                KafkaServerUri = new List<Uri> { new Uri("http://localhost:1"), new Uri("http://localhost:2") },
                KafkaConnectionFactory = _factoryMock.Object
            });

            _connMock1.Setup(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()))
                      .Throws(new ApplicationException("some error"));

            _connMock2.Setup(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()))
                      .Throws(new ApplicationException("some error"));

            router.GetTopicMetadataAsync(TestTopic).ContinueWith(t =>
            {
                Assert.That(t.IsFaulted, Is.True);
                Assert.That(t.Exception, Is.Not.Null);
                Assert.That(t.Exception.ToString(), Is.StringContaining("ServerUnreachableException"));
            }).Wait();
        }

        [Test]
        public void BrokerRouteShouldReturnTopicFromCache()
        {
            var metadataResponse = CreateMetaResponse();
            var router = CreateBasicBrokerRouter(metadataResponse);

            var result1 = router.GetTopicMetadataAsync(TestTopic).Result;
            var result2 = router.GetTopicMetadataAsync(TestTopic).Result;

            _connMock1.Verify(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()), Times.Once());

            Assert.That(result1.Count, Is.EqualTo(1));
            Assert.That(result1[0].Name, Is.EqualTo(TestTopic));
            Assert.That(result2.Count, Is.EqualTo(1));
            Assert.That(result2[0].Name, Is.EqualTo(TestTopic));
        } 
        #endregion

        #region SelectBrokerRouteAsync Exact Tests...
        [Test]
        public void SelectExactPartitionShouldReturnRequestedPartition()
        {
            var metadataResponse = CreateMetaResponse();
            var router = CreateBasicBrokerRouter(metadataResponse);

            var p0 = router.SelectBrokerRouteAsync(TestTopic, 0).Result;
            var p1 = router.SelectBrokerRouteAsync(TestTopic, 1).Result;

            Assert.That(p0.PartitionId, Is.EqualTo(0));
            Assert.That(p1.PartitionId, Is.EqualTo(1));
        }

        [Test]
        public void SelectExactPartitionShouldThrowWhenPartitionDoesNotExist()
        {
            var metadataResponse = CreateMetaResponse();
            var router = CreateBasicBrokerRouter(metadataResponse);

            router.SelectBrokerRouteAsync(TestTopic, 3)
                  .ContinueWith(t =>
                      {
                          Assert.That(t.IsFaulted, Is.True);
                          Assert.That(t.Exception, Is.Not.Null);
                          Assert.That(t.Exception.ToString(), Is.StringContaining("InvalidPartitionException"));
                      }).Wait();
        }

        [Test]
        public void SelectExactPartitionShouldThrowWhenTopicsCollectionIsEmpty()
        {
            var metadataResponse = CreateMetaResponse();
            metadataResponse.Topics.Clear();

            var router = CreateBasicBrokerRouter(metadataResponse);

            router.SelectBrokerRouteAsync(TestTopic, 1)
                  .ContinueWith(t =>
                  {
                      Assert.That(t.IsFaulted, Is.True);
                      Assert.That(t.Exception, Is.Not.Null);
                      Assert.That(t.Exception.ToString(), Is.StringContaining("InvalidTopicMetadataException"));
                  }).Wait();
        }

        [Test]
        public void SelectExactPartitionShouldThrowWhenBrokerCollectionIsEmpty()
        {
            var metadataResponse = CreateMetaResponse();
            metadataResponse.Brokers.Clear();

            var router = CreateBasicBrokerRouter(metadataResponse);

            router.SelectBrokerRouteAsync(TestTopic, 1)
                  .ContinueWith(t =>
                  {
                      Assert.That(t.IsFaulted, Is.True);
                      Assert.That(t.Exception, Is.Not.Null);
                      Assert.That(t.Exception.ToString(), Is.StringContaining("LeaderNotFoundException"));
                  }).Wait();
        }
        #endregion

        #region SelectBrokerRouteAsync Select Tests...

        [Test]
        [TestCase(null)]
        [TestCase("withkey")]
        public void SelectPartitionShouldUsePartitionSelector(string key)
        {
            var metadataResponse = CreateMetaResponse();
            _partitionSelectorMock.Setup(x => x.Select(It.IsAny<Topic>(), key))
                                  .Returns(() => new Partition
                                  {
                                      ErrorCode = 0,
                                      Isrs = new List<int> { 1 },
                                      PartitionId = 0,
                                      LeaderId = 0,
                                      Replicas = new List<int> { 1 },
                                  });
            
            var router = CreateBasicBrokerRouter(metadataResponse);

            var result = router.SelectBrokerRouteAsync(TestTopic, key).Result;

            _partitionSelectorMock.Verify(f => f.Select(It.Is<Topic>(x => x.Name == TestTopic), key), Times.Once());     
        }

        [Test]
        public void SelectPartitionShouldThrowWhenTopicsCollectionIsEmpty()
        {
            var metadataResponse = CreateMetaResponse();
            metadataResponse.Topics.Clear();

            var router = CreateBasicBrokerRouter(metadataResponse);

            router.SelectBrokerRouteAsync(TestTopic)
                  .ContinueWith(t =>
                  {
                      Assert.That(t.IsFaulted, Is.True);
                      Assert.That(t.Exception, Is.Not.Null);
                      Assert.That(t.Exception.ToString(), Is.StringContaining("InvalidTopicMetadataException"));
                  }).Wait();
        }

        [Test]
        public void SelectPartitionShouldThrowWhenBrokerCollectionIsEmpty()
        {
            var metadataResponse = CreateMetaResponse();
            metadataResponse.Brokers.Clear();

            var router = CreateBasicBrokerRouter(metadataResponse);

            router.SelectBrokerRouteAsync(TestTopic)
                  .ContinueWith(t =>
                  {
                      Assert.That(t.IsFaulted, Is.True);
                      Assert.That(t.Exception, Is.Not.Null);
                      Assert.That(t.Exception.ToString(), Is.StringContaining("LeaderNotFoundException"));
                  }).Wait();
        }
        #endregion

        #region Private Methods...
        private BrokerRouter CreateBasicBrokerRouter(MetadataResponse response)
        {
            var router = new BrokerRouter(new KafkaNet.Model.KafkaOptions
            {
                KafkaServerUri = new List<Uri> { new Uri("http://localhost:1"), new Uri("http://localhost:2") },
                KafkaConnectionFactory = _factoryMock.Object,
                PartitionSelector = _partitionSelectorMock.Object
            });

            _connMock1.Setup(x => x.SendAsync(It.IsAny<IKafkaRequest<MetadataResponse>>()))
                      .Returns(() => Task.Factory.StartNew(() => new List<MetadataResponse> { response }));

            return router;
        }

        private MetadataResponse CreateMetaResponse()
        {
            return new MetadataResponse
                {
                    CorrelationId = 1,
                    Brokers = new List<Broker>
                        {
                            new Broker
                                {
                                    Host = "localhost",
                                    Port = 1,
                                    BrokerId = 0
                                },
                            new Broker
                                {
                                    Host = "localhost",
                                    Port = 2,
                                    BrokerId = 1
                                },
                        },
                    Topics = new List<Topic>
                        {
                            new Topic
                                {
                                    ErrorCode = 0,
                                    Name = TestTopic,
                                    Partitions = new List<Partition>
                                        {
                                            new Partition
                                                {
                                                    ErrorCode = 0,
                                                    Isrs = new List<int> {1},
                                                    PartitionId = 0,
                                                    LeaderId = 0,
                                                    Replicas = new List<int> {1},
                                                },
                                            new Partition
                                                {
                                                    ErrorCode = 0,
                                                    Isrs = new List<int> {1},
                                                    PartitionId = 1,
                                                    LeaderId = 1,
                                                    Replicas = new List<int> {1},
                                                }
                                        }

                                }
                        }
                };
        } 
        #endregion
    }
}