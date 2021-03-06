﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using CSGeneral;
using ModelFramework;

// --------------------------------------------------------------------
/// <summary>
/// 
/// </summary>
// --------------------------------------------------------------------
class Factory
{
    private Instance _Root;
    private List<FactoryProperty> RegisteredProperties;
    private List<EvntHandler> RegisteredEventHandlers;
    private List<FactoryEvent> RegisteredEvents;
    private List<LinkField> Links;
    private Assembly CallingAssembly;
    // --------------------------------------------------------------------
    /// <summary>
    /// 
    /// </summary>
    // --------------------------------------------------------------------
    public Factory()
    {
        RegisteredProperties = new List<FactoryProperty>();
        RegisteredEventHandlers = new List<EvntHandler>();
        RegisteredEvents = new List<FactoryEvent>();
        Links = new List<LinkField>();
    }
    public Instance Root
    {
        get { return _Root; }
    }
    public List<FactoryProperty> Properties
    {
        get { return RegisteredProperties; }
    }
    public List<EvntHandler> EventHandlers
    {
        get { return RegisteredEventHandlers; }
    }
    public List<FactoryEvent> Events
    {
        get { return RegisteredEvents; }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Clear all the current settings for this Factory.
    /// </summary>
    // --------------------------------------------------------------------
    public void Clear()
    {
        RegisteredProperties.Clear();
        RegisteredEventHandlers.Clear();
        RegisteredEvents.Clear();
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Create instances (and populate their fields and properies of all 
    /// classes as specified by the Xml passed in. The newly created root
    /// instance can be retrieved by the 'Root' property.
    /// </summary>
    /// <param name="Xml"></param>
    /// <param name="AssemblyWithTypes"></param>
    /// <param name="HostComponent"></param>
    // --------------------------------------------------------------------
    public void Create(String Xml, Assembly AssemblyWithTypes, ApsimComponent ParentComponent)
    {
        CallingAssembly = AssemblyWithTypes;
        XmlDocument Doc = new XmlDocument();
        Doc.LoadXml(Xml);
        RemoveShortCuts(Doc.DocumentElement);
        _Root = CreateInstance(Doc.DocumentElement, null, null, ParentComponent);
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Resolve all links and initialise all Instances
    /// </summary>
    // --------------------------------------------------------------------
    public void Initialise()
    {
        ResolveLinks();
        CallInitialisedOnAll(_Root);

        for (int i = 0; i < EventHandlers.Count; i++)
        {
            if (String.Compare(EventHandlers[i].EventName, "Initialised", true) == 0)
                EventHandlers[i].Invoke(null);
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Create an instance of a the 'Instance' class based on the 
    /// Node type passed in information. Then populate the instance based
    /// on the child XML nodes.
    /// </summary>
    /// <param name="Node"></param>
    /// <param name="Parent"></param>
    /// <param name="ParentInstance"></param>
    /// <param name="HostComponent"></param>
    /// <returns></returns>
    // --------------------------------------------------------------------
    private Instance CreateInstance(XmlNode Node,
                                    XmlNode Parent,
                                    Instance ParentInstance,
                                    ApsimComponent ParentComponent)
    {
        Type ClassType = GetTypeOfChild(Node, ParentInstance); 
        if (ClassType == null)
            throw new Exception("Cannot find a class called: " + Node.Name);
        object Model = Activator.CreateInstance(ClassType);
        Instance CreatedInstance;
        if (Model.GetType().IsSubclassOf(typeof(Instance)))
        {
            CreatedInstance = (Instance)Model;
        }
        else
            CreatedInstance = new Instance(Model);

        //if (CreatedInstance.GetType().IsSubclassOf(typeof(DerivedInstance)))
        //{
        //    ((DerivedInstance)CreatedInstance).Initialise(Node, ParentInstance, ParentComponent);
        //}
        if (CreatedInstance != null)
        {
            CreatedInstance.Initialise(XmlHelper.Name(Node), ParentInstance, ParentComponent);
            GetAllProperties(CreatedInstance, Parent);
            GetAllEventHandlers(CreatedInstance);
            GetAllEvents(CreatedInstance);
            PopulateParams(CreatedInstance, Node, ParentComponent);
        }
        else
        {
            throw new Exception("Class " + Node.Name + " must be derived from the \"Instance\" class");
        }

        return CreatedInstance;
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Go through all reflected fields and properties that are tagged
    /// with a 'Param' or 'Input' attribute and add them to our list
    /// of registered properties.
    /// </summary>
    /// <param name="Obj"></param>
    /// <param name="Parent"></param>
    // --------------------------------------------------------------------
    private void GetAllProperties(Instance Obj, XmlNode Parent)
    {
        FieldInfo[] fields = Obj.Model.GetType().GetFields(BindingFlags.FlattenHierarchy | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (FieldInfo Property in fields)
        {
            bool AddProperty = false;
            bool IsOutput = false;
            Object[] Attributes = Property.GetCustomAttributes(false);
            foreach (Object Attr in Attributes)
            {
                Type attrType = Attr.GetType();
                IsOutput = (IsOutput || (Attr.GetType() == typeof(Output)));
                if (attrType == typeof(Param))
                    AddProperty = true;
                if (attrType == typeof(Input))
                    AddProperty = true;
                if (attrType == typeof(Writable))
                    AddProperty = true;
                if (attrType == typeof(Output))
                    AddProperty = true;
                if (attrType == typeof(Link))
                {
                    LinkField LinkF = new LinkField(Obj, Property, (Link)Attr);
                    Links.Add(LinkF);
                }
            }
            if (AddProperty)
            {
               
                FactoryProperty NewProperty = new FactoryProperty(new ReflectedField(Property, Obj.Model), Parent);
                if (IsOutput)
                    RemoveRegisteredOutput(NewProperty.OutputName);
                RegisteredProperties.Add(NewProperty);
            }
        }
        PropertyInfo[] properties = Obj.Model.GetType().GetProperties(BindingFlags.FlattenHierarchy | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (PropertyInfo Property in properties)
        {
            bool AddProperty = false;
            bool IsOutput = false;
            Object[] Attributes = Property.GetCustomAttributes(false);
            foreach (Object Attr in Attributes)
            {
                IsOutput = (IsOutput || (Attr.GetType() == typeof(Output)));
                if (Attr.GetType() == typeof(Param))
                    AddProperty = true;
                if (Attr.GetType() == typeof(Input))
                    AddProperty = true;
                if (Attr.GetType() == typeof(Writable))
                    AddProperty = true;
                if (Attr.GetType() == typeof(Output))
                    AddProperty = true;
                if (Attr.GetType() == typeof(Link))
                {
                    LinkField LinkF = new LinkField(Obj, Property, (Link)Attr);
                    Links.Add(LinkF);
                }
            }
            if (AddProperty)
            {
                if ((String.Compare(Property.PropertyType.Name, "String") == 0) || 
                    Property.PropertyType.IsValueType ||
                    Property.PropertyType.IsArray ||
                    Property.PropertyType.GetInterface("ApsimType") != null)
                {
                    FactoryProperty NewProperty = new FactoryProperty(new ReflectedProperty(Property, Obj.Model), Parent);
                    if (IsOutput)
                        RemoveRegisteredOutput(NewProperty.OutputName);
                    RegisteredProperties.Add(NewProperty);
                }
            }
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Remove the specified [output] from the list of registered properties.
    /// Duplicates can happen when an [output] in a base class is
    /// overridden in a derived class [output]. In this case we want the last
    /// duplicate and it superseeds previous ones (base classes)
    /// </summary>
    /// <param name="OutputName"></param>
    // --------------------------------------------------------------------
    private void RemoveRegisteredOutput(String OutputName)
    {
        for (int i = 0; i != RegisteredProperties.Count; i++)
        {
            if (RegisteredProperties[i].IsOutput &&
                String.Compare(RegisteredProperties[i].OutputName, OutputName) == 0)
            {
                RegisteredProperties.RemoveAt(i);
                return;
            }
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Goes through the model looking for all event handlers that start
    /// with 'On' and are marked 'EventHandler'
    /// </summary>
    /// <param name="Obj"></param>
    // --------------------------------------------------------------------
    private void GetAllEventHandlers(Instance Obj)
    {
        foreach (MethodInfo Method in Obj.Model.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Object[] Attributes = Method.GetCustomAttributes(false);
            foreach (Object Attr in Attributes)
            {
                if (Attr.GetType() == typeof(EventHandler)) 
                {
                    string EventName = null;
                    if ((Attr as EventHandler).EventName != "")
                        EventName = (Attr as EventHandler).EventName;
                    else if (Method.Name.Length > 2 && Method.Name.Substring(0, 2) == "On")
                        EventName = Method.Name.Substring(2);
                    
                    if (EventName != null)
                        RegisteredEventHandlers.Add(new FactoryEventHandler(Method, Obj.Model, EventName));
                }
            }
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Goes through the model looking for all events
    /// </summary>
    /// <param name="Obj"></param>
    // --------------------------------------------------------------------
    private void GetAllEvents(Instance Obj)
    {
        foreach (EventInfo Event in Obj.Model.GetType().GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Object[] Attributes = Event.GetCustomAttributes(false);
            foreach (Object Attr in Attributes)
            {
                if (Attr.GetType() == typeof(Event))
                    RegisteredEvents.Add(new FactoryEvent(Event, Obj.Model));
            }
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Go through all child XML nodes for the node passed in and set
    /// the corresponding property values in the Obj instance passed in.
    /// </summary>
    /// <param name="Obj"></param>
    /// <param name="Node"></param>
    /// <param name="HostComponent"></param>
    // --------------------------------------------------------------------
    private void PopulateParams(Instance Obj, XmlNode Node, ApsimComponent ParentComponent)
    {
        // Look for an XmlNode param. If found then given it our current 'Node'.
        bool HavePassedXMLToObj = false;
        foreach (FactoryProperty Property in RegisteredProperties)
        {
            if (( String.Compare(Property.TypeName, "XmlNode", StringComparison.Ordinal) == 0) && (Property.OutputName.Contains(Node.Name)))
            {
                Property.SetObject(Node);
                HavePassedXMLToObj = true;
            }
        }

        // Go through all child XML nodes for the node passed in and set
        // the corresponding property values in the Obj instance passed in.
        if (!HavePassedXMLToObj)
        {
            foreach (XmlNode Child in Node.ChildNodes)
            {
                if (Child.GetType() != typeof(XmlComment))
                {
                    Type t = GetTypeOfChild(Child, Obj);
                    if ((t != null) && (t.IsSubclassOf(typeof(Instance)) || t.IsClass))
                    {
                        // Create a child instance - indirect recursion.
                        Instance ChildInstance = CreateInstance(Child, Child, Obj, ParentComponent);
                        Obj.Add(ChildInstance);

                        FactoryProperty Parameter = FindProperty(Child);
                        if (XmlHelper.Name(Child).Contains("["))
                        {
                            String ArrayName = XmlHelper.Name(Child);
                            StringManip.SplitOffBracketedValue(ref ArrayName, '[', ']');
                            XmlHelper.SetName(Child, ArrayName);
                            Parameter = FindProperty(Child);
                            if (Parameter != null)
                                Parameter.AddToList(ChildInstance);
                            else
                            {
                                // Parameter must be an array link to child nodes e.g.
                                // [Link] LeafCohort[] InitialLeaves;
                            }
                        }

                        else if ((Parameter != null) && (Parameter.IsParam && !Parameter.TypeName.Contains("System::")))
                        {
                            Parameter.SetObject(ChildInstance.Model);
                        }

                    }
                    else if (Child.Name == "Memo")
                    {
                        // Ignore memo fields.
                    }
                    else 
                    {
                        FactoryProperty Parameter = FindProperty(Child);
                        if (Parameter != null)
                        {
                            Parameter.Name = XmlHelper.Name(Child);
                            Parameter.Set(Child);
                        }
                    }
                }
            }
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Return the type for the child node.
    /// </summary>
    /// <param name="Child"></param>
    /// <param name="Obj"></param>
    /// <returns></returns>
    // --------------------------------------------------------------------
    private Type GetTypeOfChild(XmlNode Child, Instance Obj)
    {
        FactoryProperty Parameter = FindProperty(Child);
        Type t = FindType(Child.Name);
        if (t != null && t.BaseType != null && t.BaseType.Name == "Attribute")  
            t = null;         // Exclude types if they are an attribute type.
        if ((t == null) && (Parameter != null))
            t = CallingAssembly.GetType(Parameter.TypeName);
        if ((t == null) && (Parameter != null))
            t = CallingAssembly.GetType(Obj.Name + "+" + Parameter.TypeName);
        if (t == null && Obj != null && 
            (Obj.Model.GetType().Name == "Script" || Obj.InstanceName.Contains("Script.")))
        {
            // check in the referenced assemblies.
            foreach (AssemblyName Reference in CallingAssembly.GetReferencedAssemblies())
            {
                Assembly A = Assembly.Load(Reference);
                t = A.GetType(Child.Name);
                if (t != null)
                    return t;
            }

        } return t;
    }
    // --------------------------------------------------------------------
    // Find the type.
    // --------------------------------------------------------------------
    private Type FindType(string typeName)
    {
        Type t = CallingAssembly.GetType(typeName);
        if (t != null || CallingAssembly.Location == string.Empty)
            return t;

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Assembly assembly in assemblies)
        {
            t = assembly.GetType(typeName);
            if (t != null)
                return t;
        }
        return null;
    }

    // --------------------------------------------------------------------
    /// <summary>
    /// Go through all our registered properties and look for the one that
    /// has the specified name. Returns null if not found.
    /// </summary>
    /// <param name="Child"></param>
    /// <returns></returns>
    // --------------------------------------------------------------------
    private FactoryProperty FindProperty(XmlNode Child)
    {
        String FQN = CalcParentName(Child).ToLower();
        foreach (FactoryProperty Property in RegisteredProperties)
        {
            if (String.Compare(Property.FQN, FQN, System.StringComparison.OrdinalIgnoreCase) == 0)
                return Property;
        }

        // Go look for the plural version - property might be an array.
        FQN = FQN + "s";
        foreach (FactoryProperty Property in RegisteredProperties)
        {
            if (String.Compare(Property.FQN, FQN, System.StringComparison.OrdinalIgnoreCase) == 0)
                return Property;
        }
        return null;
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Check the members of the array.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="Property"></param>
    /// <param name="ErrorString"></param>
    // --------------------------------------------------------------------
    protected void CheckArray<T>(FactoryProperty Property, String MsgString)
    {
        T[] arrayObj = (T[])Property.Get;
        for (int i = 0; i < arrayObj.Length; i++)
        {
            Double val = (Double)Convert.ChangeType(arrayObj[i], typeof(Double));
            if (!Double.IsNaN(Property.ParamMinVal) &&
                val < Property.ParamMinVal)
                MsgString += "The value provided for element " + i +
                                " of parameter " + Property.FQN +
                                " is less than its allowed minimum (" +
                                val + " vs. " +
                                Property.ParamMinVal + ")\n";
            if (!Double.IsNaN(Property.ParamMaxVal) &&
                val > Property.ParamMaxVal)
                MsgString += "The value provided for element " + i +
                                " of parameter " + Property.FQN +
                                " is greater than its allowed maximum (" +
                                val + " vs. " +
                                Property.ParamMaxVal + ")\n";
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Check for parameters in the model that haven't been given a value and throw if any 
    /// are found. Also do range checking, if applicable.
    /// </summary>
    // --------------------------------------------------------------------
    public void CheckParameters(ApsimComponent ModelInstance)
    {
        String Errors = "";
        String RangeMsgs = "";
        for (int i = 0; i != RegisteredProperties.Count; i++)
        {
            FactoryProperty Property = RegisteredProperties[i];
            if (Property.IsParam)
            {
                if (!Property.HasAsValue && !Property.OptionalParam)
                {
                    if (Errors != "")
                        Errors += ", ";
                    Errors += Property.FQN;
                }
                // Is there a tidier way to do this?
                if (Property.HasAsValue &&
                    (!Double.IsNaN(Property.ParamMinVal) ||
                    !Double.IsNaN(Property.ParamMaxVal)))
                {
                    if (Property.TypeName == "Double" ||
                        Property.TypeName == "Single" ||
                        Property.TypeName == "Int32")
                    {
                        double val = Convert.ToDouble(Property.Get.ToString());
                        if (!Double.IsNaN(Property.ParamMinVal) &&
                            val < Property.ParamMinVal)
                            RangeMsgs += "The value provided for parameter " + Property.FQN +
                                " is less than its allowed minimum (" +
                                Property.Get.ToString() + " vs. " +
                                Property.ParamMinVal + ")\n";
                        if (!Double.IsNaN(Property.ParamMaxVal) &&
                            val > Property.ParamMaxVal)
                            RangeMsgs += "The value provided for parameter " + Property.FQN +
                                " is greater than its allowed maximum (" +
                                Property.Get.ToString() + " vs. " +
                                Property.ParamMaxVal + ")\n";
                    }
                    else if (Property.TypeName == "Double[]")
                        CheckArray<double>(Property, RangeMsgs);
                    else if (Property.TypeName == "Single[]")
                        CheckArray<float>(Property, RangeMsgs);
                    else if (Property.TypeName == "Int32[]")
                        CheckArray<int>(Property, RangeMsgs);
                }
            }
        }
        if (Errors != "")
            throw new Exception("The following parameters haven't been initialised: " + Errors);
        if (RangeMsgs != "")
        {
            string rangeMessage = "In " + Root.InstanceName + ", the following parameters are outside their allowable ranges:\n" + RangeMsgs;
            if (ModelInstance != null)
                ModelInstance.Warning(rangeMessage);
            else
                throw new Exception(rangeMessage);
        }
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Check for parameters in the model that
    /// haven't been given a value and throw if any 
    /// are found.
    /// Also do range checking, if applicable.
    /// </summary>
    // --------------------------------------------------------------------
    /*    public void ThrowOnUnInitialisedParameters()
        {
            String Errors = "";
            for (int i = 0; i != RegisteredProperties.Count; i++)
            {
                FactoryProperty Property = RegisteredProperties[i];
                if (Property.IsParam && !Property.HasAsValue)
                {
                    if (Errors != "")
                        Errors += ", ";
                    Errors += Property.FQN;
                }
            }
            if (Errors != "")
                throw new Exception("The following parameters haven't been initialised: " + Errors);
        } */
    // --------------------------------------------------------------------      
    /// <summary>
    /// Remove any shortcut nodes in the children of
    /// the specified node.
    /// </summary>
    /// <param name="Node"></param>
    // --------------------------------------------------------------------
    private void RemoveShortCuts(XmlNode Node)
    {
        String ShortCutPath = XmlHelper.Attribute(Node, "shortcut");
        if (ShortCutPath != "")
        {
            // Shortcut strings will be a full path e.g. /FrenchBean/Model/Plant/Phenology/ThermalTime
            // But our Node->OwnerDocument->DocumentElemen is <Plant> 
            // So we need to find /Plant/ and remove everything on the path before that. This way
            // we'll end up with a relative path e.g. Plant/Phenology/ThermalTime
            int PosPlant = ShortCutPath.IndexOf("/Plant/");
            if (PosPlant == -1)
                throw new Exception("Invalid shortcut path: " + ShortCutPath);
            ShortCutPath = ShortCutPath.Remove(0, PosPlant + 7); // Get rid of the /Plant/ string.

            XmlNode ReferencedNode = XmlHelper.Find(Node.OwnerDocument.DocumentElement, ShortCutPath);
            if (ReferencedNode == null)
                throw new Exception("Cannot find short cut node: " + ShortCutPath);
            XmlNode NewNode = ReferencedNode.CloneNode(true);
            Node.ParentNode.ReplaceChild(NewNode, Node);
            if (XmlHelper.Attribute(Node, "name") != "")
                XmlHelper.SetName(NewNode, XmlHelper.Name(Node));
        }

        for (int i = 0; i < Node.ChildNodes.Count; i++)
            RemoveShortCuts(Node.ChildNodes[i]);

    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Resolve all [Links].
    /// </summary>
    // --------------------------------------------------------------------
    public void ResolveLinks()
    {
        for (int i = 0; i < Links.Count; i++)
            Links[i].Resolve();
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Calculate a parent name
    /// </summary>
    /// <param name="Node"></param>
    /// <returns></returns>
    // --------------------------------------------------------------------
    private String CalcParentName(XmlNode Node)
    {
        String ParentName = "";
        if (Node == null || Node.ParentNode == null || Node.ParentNode.ParentNode == null)
            return "";

        ParentName = CalcParentName(Node.ParentNode);
        return ParentName + XmlHelper.Name(Node);
    }
    // --------------------------------------------------------------------
    /// <summary>
    /// Do a depth first walk of the Instance tree, calling each Instance's Initialised method.
    /// </summary>
    // --------------------------------------------------------------------
    private void CallInitialisedOnAll(Instance Obj)
    {
        foreach (Instance Child in Obj.Children)
            CallInitialisedOnAll(Child);

        Obj.Initialised();
    }
}
