﻿using Bee.Eee.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;

namespace CaveSpy
{
    [Serializable]
    class Map
    {
        private Dictionary<string, object> _properties;
        public Map()
        {
            _properties = new Dictionary<string, object>();
        }

        public double[] elevations;
        public byte[] classifications;
        public Color[] colors;
        public string zone;
        public int height;
        public int width;
        public double physicalLeft;
        public double physicalTop;
        public double physicalRight;
        public double physicalBottom;
        public double physicalWidth;
        public double physicalHeight;
        public double physicalHigh;
        public double physicalLow;

        public void SetProperty<T>(string name, T value)
        {
            _properties[name] = (object)value;
        }

        public T GetProperty<T>(string name)
        {
            object value;
            value = _properties[name];
            return (T)value;
        }

        public void Save(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                var writer = new BinaryFormatter();
                writer.Serialize(stream, this);
            }
        }

        public static Map Load(string filePath, ILogger logger)
        {
            using (var stream = new FileStream(filePath, FileMode.Open))
            {
                var reader = new BinaryFormatter();
                var map = (Map)reader.Deserialize(stream);
                return map;
            }
        }


    }

    [Serializable]
    class Color
    {
        public ushort red;
        public ushort green;
        public ushort blue;

        public Color(ushort red, ushort green, ushort blue)
        {
            this.red = red;
            this.green = green;
            this.blue = blue;
        }
    }

    class MapAlgorithms
    {
        private ILogger _logger;

        public MapAlgorithms(ILogger logger)
        {
            _logger = (logger != null) ? logger.CreateSub("MapAlgorithms") : throw new ArgumentNullException("logger");
        }
        public void ReadCloudIntoMap(Map map, int mapWidth, PointCloud cloud)
        {
            var header = cloud.Header;

            map.physicalLeft = header.MinX;
            map.physicalTop = header.MinY;
            map.physicalRight = header.MaxX;
            map.physicalBottom = header.MaxY;
            map.physicalWidth = header.MaxX - header.MinX;
            map.physicalHeight = header.MaxY - header.MinY;
            map.physicalHigh = cloud.Header.MaxZ;
            map.physicalLow = cloud.Header.MinZ;
            map.zone = cloud.Zone;

            map.width = mapWidth;
            map.height = (int)(int)(mapWidth / map.physicalWidth * map.physicalHeight);

            double xScale = (double)(map.width - 1) / map.physicalWidth;
            double yScale = (double)(map.height - 1) / map.physicalHeight;
            double minX = map.physicalLeft;
            double minY = map.physicalTop;

            int size = map.width * map.height;
            map.elevations = new double[size];
            map.classifications = new byte[size];
            map.colors = new Color[size];
            var contributers = new int[size];


            for (int i = 0; i < cloud.Header.NumberOfPointRecords; i++)
            {
                if (i % 1000000 == 0)
                    _logger.Log(Level.Debug, $"{i:0,000}/{header.NumberOfPointRecords:0,000} {(double)i / (double)header.NumberOfPointRecords * 100: 0.00}%");

                var p = cloud.Points[i];

                double x = p.X * header.XScaleFactor + header.XOffset;
                double y = p.Y * header.YScaleFactor + header.YOffset;
                double z = p.Z * header.ZScaleFactor + header.ZOffset;

                // skip if not in area of interest
                if (x < map.physicalLeft || x > map.physicalRight || y < map.physicalTop || y > map.physicalBottom)
                    continue;

                int xi = (int)((x - minX) * xScale);
                int yi = map.height - (int)((y - minY) * yScale) - 1;
                int ii = yi * map.width + xi;
                map.elevations[ii] += z;
                contributers[ii]++;
                map.classifications[ii] = p.Classification;
                if (header.PointDataFormat == 2 || header.PointDataFormat == 3)
                    map.colors[ii] = new Color(p.Red, p.Green, p.Blue);
            }

            for (int i = 0; i < map.elevations.Length; i++)
            {
                int c = contributers[i];
                if (c > 1)
                    map.elevations[i] /= contributers[i];
            }
        }

        public void LinearFillMap(Map map)
        {
            int i = 0;
            double lastValue = 0;
            for (int y = 0; y < map.height; y++)
            {
                for (int x = 0; x < map.width; x++, i++)
                {
                    double v = map.elevations[i];
                    if (v == 0)
                        map.elevations[i] = lastValue;
                    else
                        lastValue = v;
                }
            }
        }

        public void EdgeFillMapAlgorithm(Map map)
        {
            // find all the edges in current map
            var e = map.elevations;
            var c = map.classifications;
            var origEdges = new List<Grower>();

            for (int y = 1; y < map.height - 1; y++)
            {
                for (int x = 1; x < map.width - 1; x++)
                {
                    int i = x + y * map.width;
                    bool isEdge = (c[i] != 0 && (
                        c[i - map.width - 1] == 0 || c[i - map.width] == 0 || c[i - map.width + 1] == 0 ||
                        c[i - 1] == 0 || c[i + 1] == 0 ||
                        c[i + map.width - 1] == 0 || c[i + map.width] == 0 || c[i + map.width + 1] == 0));

                    if (isEdge)
                        origEdges.Add(new Grower() { x = x, y = y, i = i });
                }
            }

            // start filling the holes via copying self into holes until there are no more edges left
            int iterCount = 0;
            while (origEdges.Count > 0)
            {
                Dictionary<int, Grower> newEdges = new Dictionary<int, Grower>();

                // create new edges
                int nextRow = map.width - 2;
                for (int i = 0; i < origEdges.Count; i++)
                {
                    var g = origEdges[i];
                    var gv = e[g.i];
                    for (int y = g.y - 1; y <= g.y + 1; y++)
                    {
                        for (int x = g.x - 1; x <= g.x + 1; x++)
                        {
                            int ii = y * map.width + x;

                            if ((x != g.x || y != g.y) && (x >= 0 && x < map.width && y >= 0 && y < map.height) && c[ii] == 0)
                            {
                                Grower ng = null;
                                if (newEdges.TryGetValue(ii, out ng))
                                {
                                    ng.value += gv;
                                    ng.contributerCount++;
                                }
                                else
                                {
                                    ng = new Grower() { x = x, y = y, i = ii, value = gv, contributerCount = 1 };
                                    newEdges.Add(ii, ng);
                                }
                            }
                        }
                    }
                }

                // fill in the calculated values
                origEdges = newEdges.Values.ToList();
                newEdges.Clear();

                for (int i = 0; i < origEdges.Count; i++)
                {
                    var edge = origEdges[i];
                    e[edge.i] = edge.value / edge.contributerCount;
                    c[edge.i] = 13; // custom to show that we filled the edge
                }

                iterCount++;
                if (iterCount % 10 == 0)
                    _logger.Log(Level.Debug, $"Fill iteration {iterCount} edge count {origEdges.Count}");
            }
        }

        class Grower
        {
            public int x;
            public int y;
            public int i;
            public double value;
            public int contributerCount;
        }

        public void GeometricMeanFilter(Map map, int N)
        {
            double[] newElevations = new double[map.width * map.height];
            int N2 = N / 2;
            for (int y = 0; y < map.height; y++)
            {
                for (int x = 0; x < map.width; x++)
                {
                    double Z = 1.0;
                    int cnt = 0;
                    for (int i = -N2; i <= N2; i++)
                    {
                        for (int j = -N2; j <= N2; j++)
                        {
                            int xx = i + x;
                            int yy = j + y;
                            if (xx < 0 || xx >= map.width || yy < 0 || yy >= map.height)
                                continue;

                            cnt++;
                            Z *= map.elevations[xx + yy * map.width]+1;
                        }
                    }

                    newElevations[x + y * map.width] = Math.Pow(Z, 1.0 / (double)cnt) - 1;
                }
            }

            map.elevations = newElevations;
        }

        public Map GenerateMap(string type, int width, int height)
        {
            switch (type)
            {
                case "fill-test":
                    return FillTestMap(width, height);
                default:
                    return null;
            }
        }

        private Map FillTestMap(int width, int height)
        {
            Map map = new Map();
            map.physicalBottom = 100;
            map.physicalTop = 0;
            map.physicalLeft = 0;
            map.physicalRight = 100;
            map.physicalWidth = 100;
            map.physicalHeight = 100;
            map.width = width;
            map.height = height;
            map.zone = "12T";
            map.elevations = new double[width * height];
            map.classifications = new byte[width * height];

            for (int i = 0; i < map.elevations.Length; i++)
            {
                map.elevations[i] = 100;
                map.classifications[i] = (byte)1;
            }

            // put in some random holes
            Random r = new Random();
            HashSet<int> used = new HashSet<int>();
            for (int i = 0; i < 100; i++)
            {
                // get 
                int ii;
                while (used.Contains(ii = (int)(r.NextDouble() * map.elevations.Length)));
                used.Add(ii);
                map.elevations[ii] = 0;
                map.classifications[ii] = 0;
            }

            // put in a few large holes
            for (int i = 0; i < 10; i++)
            {
                int x1 = r.Next(map.width);
                int y1 = r.Next(map.height);
                int rad = r.Next(100) + 10;
                CutCircle(map, x1, y1, rad);
            }

            return map;
        }

        private void CutCircle(Map map, int x1, int y1, int rad)
        {
            int rad2 = rad * rad;
            int x2 = x1 + rad * 2;
            int y2 = y1 + rad * 2;

            int xc = (x1 + x2) / 2;
            int yc = (y1 + y2) / 2;

            for (int yy = y1; yy <= y2; yy++)
            {
                for (int xx = x1; xx <= x2; xx++)
                {
                    if (xx < 0 || xx >= map.width || yy < 0 || yy >= map.height)
                        continue;

                    if ((xx - xc) * (xx - xc) + (yy - yc) * (yy - yc) < rad2)
                    {
                        int ii = xx + yy * map.width;
                        map.elevations[ii] = 0;
                        map.classifications[ii] = 0;
                    }
                }
            }
        }
    }
}
