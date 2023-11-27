// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;

namespace XMAT.Scripting
{
    public class ScriptTypeCollection : ObservableCollection<ScriptTypeInfo>
    {
        public ScriptTypeCollection(Type[] types)
        {
            if(types != null)
                GetClassInfo(types);
        }

        private void GetClassInfo(Type[] types)
        {
            foreach(Type t in types)
            {
                var desc = Attribute.GetCustomAttribute(t, typeof(DescriptionAttribute)) as DescriptionAttribute;
                var name = Attribute.GetCustomAttribute(t, typeof(DisplayAttribute)) as DisplayAttribute;

                var ti = new ScriptTypeInfo
                {
                    Name = name != null ? name.Name : t.Name,
                    Type = t.Name,
                    Description = desc != null ? Localization.GetLocalizedString(desc.Description) : String.Empty,
                    Properties = new ScriptTypeCollection(null)
                };

                var props = t.GetProperties();

                foreach(PropertyInfo pi in props)
                {
                    var propDesc = pi.GetCustomAttributes(typeof(DescriptionAttribute)).FirstOrDefault() as DescriptionAttribute;
                    var tiProp = new ScriptTypeInfo
                    {
                        Name = pi.Name,
                        Type = pi.PropertyType.Name,
                        Description = propDesc != null ? Localization.GetLocalizedString(propDesc.Description) : String.Empty,
                    };
                    ti.Properties.Add(tiProp);
                }

                //var methods = t.GetMethods();

                //foreach (MethodInfo mi in methods)
                //{
                //    if(mi.IsSpecialName || mi.IsPrivate)
                //        continue;

                //    var miProp = new ScriptTypeInfo
                //    {
                //        Name = mi.Name,
                //        Type = mi.MemberType.ToString(),
                //        Description = string.Empty
                //    };
                //    ti.Properties.Add(miProp);
                //}

                this.Add(ti);
            }
        }
    }
}
