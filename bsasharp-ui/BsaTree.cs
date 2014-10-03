﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using BSAsharp;

namespace bsasharp_ui
{
    class BsaTree
    {
        private readonly Bsa _bsa;
        private readonly IDictionary<string, object> _bsaExpando;

        public BsaTree(Bsa bsa)
        {
            _bsa = bsa;
            _bsaExpando = new ExpandoObject();

            IDictionary<string, object> /*previousExpando, */currentExpando;
            foreach (var folder in bsa)
            {
                currentExpando = _bsaExpando;

                var splitPath = folder.Path.Split('\\');
                foreach (var chunk in splitPath)
                {
                    //previousExpando = currentExpando;
                    if (!currentExpando.ContainsKey(chunk))
                        currentExpando.Add(chunk, (currentExpando = new ExpandoObject()));
                }
            }

            Nodes = _bsaExpando.Select(exp => new TreeNode(exp.Key) { Tag = exp.Value });
        }

        public IEnumerable<TreeNode> Nodes { get; private set; }
    }
}
