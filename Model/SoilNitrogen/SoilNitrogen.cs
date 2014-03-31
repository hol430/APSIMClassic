﻿using System;
using System.Reflection;
using System.Collections.Generic;
using System.Xml.Serialization;
using System.Xml;
using System.Linq;
using ModelFramework;
using CSGeneral;

/// <summary>
/// Computes the soil C and N processes
/// </summary>
/// <remarks>
/// Further tidying up of code, basically eliminating non-implemented functions/variables and replacing old functions for new ones
///   Deletion and replacement should not alter any existing capability, only make the code cleaner and thus easier to further development
///   Functions replaced are now more generic, so can be re-used (e.g. temperature factor etc)
/// Based on a more-or-less direct port of the Fortran SoilN model  -  Ported by Eric Zurcher Sept/Oct 2010
/// Code tidied up initilly by RCichota on Aug/Sep-2012 (updates in Feb-Mar/2014)  in the future)
/// </remarks>
public partial class SoilNitrogen
{
    #region >>  Events which we publish

    /// <summary>
    /// Event to communicate other modules of C and/or N changes to/from outside the simulation
    /// </summary>
    [Event]
    public event ExternalMassFlowDelegate ExternalMassFlow;

    /// <summary>
    /// Event to communicate other modules that solutes have been added to the simulation (owned by SoilNitrogen)
    /// </summary>
    [Event]
    public event NewSoluteDelegate new_solute;

    /// <summary>
    /// Event to comunicate other modules (SurfaceOM) that residues have been decomposed
    /// </summary>
    [Event]
    public event SurfaceOrganicMatterDecompDelegate actualResidueDecompositionCalculated;

    #endregion events published

    #region >>  Setup events handlers and methods

    /// <summary>
    /// Sets the commands for the initial setup
    /// </summary>
    [EventHandler]
    public void OnInitialised()
    {
        // Variable handling when using APSIMX
#if (APSIMX == true)
        initDone = false;
        ApsimFile.Soil Soil = (ApsimFile.Soil)Paddock.Get("Soil");
        dlayer = Soil.Thickness;
        SoilDensity = Soil.Water.BD;
        sat_dep = MathUtility.Multiply(Soil.Water.SAT, Soil.Thickness);
        dul_dep = MathUtility.Multiply(Soil.Water.DUL, Soil.Thickness);
        ll15_dep = MathUtility.Multiply(Soil.Water.LL15, Soil.Thickness);
        sw_dep = MathUtility.Multiply(Soil.SW, Soil.Thickness);
        oc = Soil.OC;
        ph = Soil.Analysis.PH;
        salb = Soil.SoilWater.Salb;
        no3ppm = Soil.NO3;
        nh4ppm = Soil.NH4;

        fbiom = Soil.SoilOrganicMatter.FBiom;
        finert = Soil.SoilOrganicMatter.FInert;
        HumusCNr = Soil.SoilOrganicMatter.SoilCN;
        InitialFOMAmount = Soil.SoilOrganicMatter.RootWt;
        InitialCNrFOM = Soil.SoilOrganicMatter.RootCN;
        enr_a_coeff = Soil.SoilOrganicMatter.EnrACoeff;
        enr_b_coeff = Soil.SoilOrganicMatter.EnrBCoeff;
        Clock.Tick += new TimeDelegate(OnTick);
        Clock.Process += new NullTypeDelegate(OnProcess);
        //Clock.Post += new NullTypeDelegate(OnPost);

        initDone = true;
#endif

        // set the size of arrays
        ResizeLayeredVariables(dlayer.Length);

        // check few initialisation parameters
        CheckParameters();

        // check the initial values of some basic variables
        CheckInitialVariables();

        // set the variables up with their the initial values
        SetInitialValues();

        // initialise soil temperature
        if (usingSimpleSoilTemp)
        {
            simpleST = new simpleSoilTemp(MetFile.Latitude, MetFile.tav, MetFile.amp, MetFile.MinT, MetFile.MaxT);
            Tsoil = simpleST.SoilTemperature(Clock.Today, MetFile.MinT, MetFile.MaxT, MetFile.Radn, salb, dlayer, SoilDensity, ll15_dep, sw_dep);
        }

        // notifify apsim about solutes
        AdvertiseMySolutes();

        // print SoilN report
        WriteSummaryReport();
    }

    /// <summary>
    /// Sets the commands for reseting the module to the initial setup
    /// </summary>
    [EventHandler(EventName = "reset")]
    public void OnReset()
    {
        isResetting = true;

        // Save the present C and N status
        StoreStatus();

        // reset the size of arrays
        ResizeLayeredVariables(dlayer.Length);

        // reset C and N variables to their initial state
        oc = reset_oc;
        no3ppm = reset_no3ppm;
        nh4ppm = reset_nh4ppm;
        ureappm = reset_ureappm;

        // perform initialisation and setup
        SetInitialValues();

        // reset soil temperature
        if (usingSimpleSoilTemp)
        {
            simpleST = new simpleSoilTemp(MetFile.Latitude, MetFile.tav, MetFile.amp, MetFile.MinT, MetFile.MaxT);
            Tsoil = simpleST.SoilTemperature(Clock.Today, MetFile.MinT, MetFile.MaxT, MetFile.Radn, salb, dlayer, SoilDensity, ll15_dep, sw_dep);
        }

        // get the changes of state and publish (let other component to know)
        SendDeltaState();

        // print SoilN report
        WriteSummaryReport();

        isResetting = false;
    }

    /// <summary>
    /// Checks general initialisation parameters, and let user know of some settings
    /// </summary>
    private void CheckParameters()
    {
        Console.WriteLine();
        Console.WriteLine("        - Reading/checking parameters");

        SoilNParameterSet = SoilNParameterSet.Trim();
        Console.WriteLine("          - Using " + SoilNParameterSet + " SoilN parameter set specification");

        // check whether soil temperature is present. If not, check whether the basic params for simpleSoilTemp have been supplied
        if (SimpleSoilTempAllowed)
            usingSimpleSoilTemp = (ave_soil_temp == null);

        if (usingSimpleSoilTemp)
        {
            if (MetFile.Latitude == null)
                throw new Exception("Value for latitude was not supplied");
            if (MetFile.tav == null)
                throw new Exception("Value for TAV was not supplied");
            if (MetFile.amp == null)
                throw new Exception("Value for AMP was not supplied");
        }

        int nLayers = dlayer.Length;

        // check whether ph is supplied, use a default if not - might be better to throw an exception?
        usingSimpleSoilpH = (ph == null);
        if (usingSimpleSoilpH)
        {
            ph = new double[nLayers];
            for (int layer = 0; layer < nLayers; ++layer)
                ph[layer] = defaultInitialpH;
            Console.WriteLine("          - Soil pH was not supplied, the value " + defaultInitialpH.ToString("0.00") + " will be used for all layers");
        }

        // Check if all fom values have been supplied
        if (fract_carb.Length != fom_types.Length)
            throw new Exception("Number of \"fract_carb\" different to \"fom_type\"");
        if (fract_cell.Length != fom_types.Length)
            throw new Exception("Number of \"fract_cell\" different to \"fom_type\"");
        if (fract_lign.Length != fom_types.Length)
            throw new Exception("Number of \"fract_lign\" different to \"fom_type\"");

        // Check if all C:N values have been supplied. If not use average C:N ratio in all pools
        if (fomPoolsCNratio == null || fomPoolsCNratio.Length < 3)
        {
            fomPoolsCNratio = new double[3];
            for (int i = 0; i < 3; i++)
                fomPoolsCNratio[i] = InitialCNrFOM;
        }

        // Check if initial fom depth has been supplied, if not assume that initial fom is distributed over the whole profile
        if (InitialFOMDepth <= epsilon)
            InitialFOMDepth = SumDoubleArray(dlayer);

        // Calculate conversion factor from kg/ha to ppm (mg/kg)
        convFactor = new double[nLayers];
        for (int layer = 0; layer < nLayers; ++layer)
            convFactor[layer] = MathUtility.Divide(100.0, SoilDensity[layer] * dlayer[layer], 0.0);
    }

    /// <summary>
    /// Checks whether initial values for OM and mineral N were given and make sure all layers have valid values
    /// </summary>
    /// <remarks>
    /// Initial OC values are mandatory, but not for all layers. Zero is assumed for layers not set.
    /// Initial values for mineral N are optional, assume zero if not given
    /// The inital FOM values are given as a total amount which is distributed using an exponential function.
    /// In this procedure the fraction of total FOM that goes in each layer is also computed
    /// </remarks>
    private void CheckInitialVariables()
    {
        int nLayers = dlayer.Length;

        // ensure that array for initial OC have a value for each layer
        if (reset_oc.Length < nLayers)
            Console.WriteLine("           + Values supplied for the initial OC content do not cover all layers - zero will be assumed");
        else if (reset_oc.Length > nLayers)
            Console.WriteLine("           + More values were supplied for the initial OC content than the number of layers - excess will ignored");
        Array.Resize(ref reset_oc, nLayers);

        // ensure that array for initial urea content have a value for each layer
        if (reset_ureappm == null)
            Console.WriteLine("           + No values were supplied for the initial content of urea - zero will be assumed");
        else if (reset_ureappm.Length < nLayers)
            Console.WriteLine("            + Values supplied for the initial content of urea do not cover all layers - zero will be assumed");
        else if (reset_ureappm.Length > nLayers)
            Console.WriteLine("           + More values were supplied for the initial content of urea than the number of layers - excess will ignored");
        Array.Resize(ref reset_ureappm, nLayers);

        // ensure that array for initial content of NH4 have a value for each layer
        if (reset_nh4ppm == null)
            Console.WriteLine("           + No values were supplied for the initial content of nh4 - zero will be assumed");
        else if (reset_nh4ppm.Length < nLayers)
            Console.WriteLine("            + Values supplied for the initial content of nh4 do not cover all layers - zero will be assumed");
        else if (reset_nh4ppm.Length > nLayers)
            Console.WriteLine("           + More values were supplied for the initial content of nh4 than the number of layers - excess will ignored");
        Array.Resize(ref reset_nh4ppm, nLayers);

        // ensure that array for initial content of NO3 have a value for each layer
        if (reset_no3ppm == null)
            Console.WriteLine("           + No values were supplied for the initial content of no3 - zero will be assumed");
        else if (reset_no3ppm.Length < nLayers)
            Console.WriteLine("           + Values supplied for the initial content of no3 do not cover all layers - zero will be assumed");
        else if (reset_no3ppm.Length > nLayers)
            Console.WriteLine("           + More values were supplied for the initial content of no3 than the number of layers - excess will ignored");
        Array.Resize(ref reset_no3ppm, nLayers);

        // compute initial FOM distribution in the soil (FOM fraction)
        FOMiniFraction = new double[nLayers];
        double totFOMfraction = 0.0;
        int deepestLayer = getCumulativeIndex(InitialFOMDepth, dlayer);
        double cumDepth = 0.0;
        double FracLayer = 0.0;
        for (int layer = 0; layer <= deepestLayer; layer++)
        {
            FracLayer = Math.Min(1.0, MathUtility.Divide(InitialFOMDepth - cumDepth, dlayer[layer], 0.0));
            cumDepth += dlayer[layer];
            FOMiniFraction[layer] = FracLayer * Math.Exp(-FOMDistributionCoefficient * Math.Min(1.0, MathUtility.Divide(cumDepth, InitialFOMDepth, 0.0)));
        }

        // distribute FOM through layers
        totFOMfraction = SumDoubleArray(FOMiniFraction);
        for (int layer = 0; layer <= deepestLayer; layer++)
        {
            FOMiniFraction[layer] /= totFOMfraction;
        }

        // initialise some residue decompositon variables
        residueName = new string[1] { "none" };
        pot_c_decomp = new double[1] { 0.0 };
    }

    /// <summary>
    /// Performs the initial setup and calculations
    /// </summary>
    /// <remarks>
    /// This procedure is also used onReset
    /// </remarks>
    private void SetInitialValues()
    {
        // general variables
        int nLayers = dlayer.Length;

        // convert and set C an N values over the profile
        for (int layer = 0; layer < nLayers; layer++)
        {
            // convert the amounts of mineral N
            double iniUrea = MathUtility.Divide(reset_ureappm[layer], convFactor[layer], 0.0);         // convert from ppm to kg/ha
            double iniNH4 = MathUtility.Divide(reset_nh4ppm[layer], convFactor[layer], 0.0);
            double iniNO3 = MathUtility.Divide(reset_no3ppm[layer], convFactor[layer], 0.0);

            // calculate total soil C
            double Soil_OC = reset_oc[layer] * 10000;					    // = (oc/100)*1000000 - convert from % to ppm
            Soil_OC = MathUtility.Divide(Soil_OC, convFactor[layer], 0.0);  //Convert from ppm to kg/ha

            // calculate inert soil C
            double InertC = finert[layer] * Soil_OC;

            // calculate microbial biomass C and N
            double BiomassC = MathUtility.Divide((Soil_OC - InertC) * fbiom[layer], 1.0 + fbiom[layer], 0.0);
            double BiomassN = MathUtility.Divide(BiomassC, MBiomassCNr, 0.0);

            // calculate C and N values for active humus
            double HumusC = Soil_OC - BiomassC;
            double HumusN = MathUtility.Divide(HumusC, HumusCNr, 0.0);

            // distribute C over fom pools
            double[] fomPool = new double[3];
            fomPool[0] = InitialFOMAmount * FOMiniFraction[layer] * fract_carb[FOMtypeID_reset] * defaultCarbonInFOM;
            fomPool[1] = InitialFOMAmount * FOMiniFraction[layer] * fract_cell[FOMtypeID_reset] * defaultCarbonInFOM;
            fomPool[2] = InitialFOMAmount * FOMiniFraction[layer] * fract_lign[FOMtypeID_reset] * defaultCarbonInFOM;

            // set the initial values across patches
            _urea[layer] = iniUrea;
            _nh4[layer] = iniNH4;
            _no3[layer] = iniNO3;
            inert_c[layer] = InertC;
            biom_c[layer] = BiomassC;
            biom_n[layer] = BiomassN;
            hum_c[layer] = HumusC;
            hum_n[layer] = HumusN;
            fom_c_pool[0][layer] = fomPool[0];
            fom_c_pool[1][layer] = fomPool[1];
            fom_c_pool[2][layer] = fomPool[2];
            fom_n_pool[0][layer] = MathUtility.Divide(fomPool[0], fomPoolsCNratio[0], 0.0);
            fom_n_pool[1][layer] = MathUtility.Divide(fomPool[1], fomPoolsCNratio[1], 0.0);
            fom_n_pool[2][layer] = MathUtility.Divide(fomPool[2], fomPoolsCNratio[2], 0.0);
        }

        initDone = true;

        // Store today's values (needed for deltas and NEW sysbal component)
        StoreStatus();
    }

    /// <summary>
    /// Sets the size of arrays (with nLayers)
    /// </summary>
    /// <remarks>
    /// This is used during initialisation and whenever the soil profile changes (thus not often at all)
    /// </remarks>
    /// <param name="nLayers">The number of layers</param>
    private void ResizeLayeredVariables(int nLayers)
    {
        // Amounts - N
        Array.Resize(ref _nh4, nLayers);
        Array.Resize(ref _no3, nLayers);
        Array.Resize(ref _urea, nLayers);
        Array.Resize(ref TodaysInitialNO3, nLayers);
        Array.Resize(ref TodaysInitialNH4, nLayers);
        Array.Resize(ref fom_n_pool[0], nLayers);
        Array.Resize(ref fom_n_pool[1], nLayers);
        Array.Resize(ref fom_n_pool[2], nLayers);
        Array.Resize(ref biom_n, nLayers);
        Array.Resize(ref hum_n, nLayers);

        // Amounts - C
        Array.Resize(ref fom_c_pool[0], nLayers);
        Array.Resize(ref fom_c_pool[1], nLayers);
        Array.Resize(ref fom_c_pool[2], nLayers);
        Array.Resize(ref inert_c, nLayers);
        Array.Resize(ref biom_c, nLayers);
        Array.Resize(ref hum_c, nLayers);

        // deltas
        Array.Resize(ref dlt_c_res_to_biom, nLayers);
        Array.Resize(ref dlt_c_res_to_hum, nLayers);
        Array.Resize(ref dlt_c_res_to_atm, nLayers);
        Array.Resize(ref dlt_res_nh4_min, nLayers);
        Array.Resize(ref dlt_res_no3_min, nLayers);

        Array.Resize(ref dlt_urea_hydrolysis, nLayers);
        Array.Resize(ref dlt_nitrification, nLayers);
        Array.Resize(ref dlt_n2o_nitrif, nLayers);
        Array.Resize(ref dlt_no3_dnit, nLayers);
        Array.Resize(ref dlt_n2o_dnit, nLayers);
        Array.Resize(ref dlt_fom_n_min, nLayers);
        Array.Resize(ref dlt_biom_n_min, nLayers);
        Array.Resize(ref dlt_hum_n_min, nLayers);
        Array.Resize(ref nh4_deficit_immob, nLayers);
        for (int i = 0; i < 3; i++)
        {
            Array.Resize(ref dlt_c_fom_to_biom[i], nLayers);
            Array.Resize(ref dlt_c_fom_to_hum[i], nLayers);
            Array.Resize(ref dlt_c_fom_to_atm[i], nLayers);
            Array.Resize(ref dlt_n_fom[i], nLayers);
        }
        Array.Resize(ref dlt_biom_c_hum, nLayers);
        Array.Resize(ref dlt_biom_c_atm, nLayers);
        Array.Resize(ref dlt_hum_c_biom, nLayers);
        Array.Resize(ref dlt_hum_c_atm, nLayers);

        Array.Resize(ref InhibitionFactor_Nitrification, nLayers);
    }

    /// <summary>
    /// Clear (zero out) the values of variables storing deltas
    /// </summary>
    /// <remarks>
    /// This is used to zero out the variables that need reseting every day, those that are not necessarily computed everyday
    /// </remarks>
    private void ClearDeltaVariables()
    {
        // residue decomposition
        Array.Clear(pot_c_decomp, 0, pot_c_decomp.Length);
        // this is also cleared onPotentialResidueDecompositionCalculated, but it is here to ensure it will be reset every timestep

        // miscelaneous
        Array.Clear(InhibitionFactor_Nitrification, 0, InhibitionFactor_Nitrification.Length);
        dlt_n_loss_in_sed = 0.0;
        dlt_c_loss_in_sed = 0.0;
    }

    /// <summary>
    /// Notifies any interested module about this module's ownership of solute information.
    /// </summary>
    private void AdvertiseMySolutes()
    {

        if (new_solute != null)
        {
            string[] solute_names;
            if (OrganicSolutesAllowed)
            {
                solute_names = new string[7] { "urea", "nh4", "no3", "org_c_pool1", "org_c_pool2", "org_c_pool3", "org_n" };
            }
            else
            { // don't publish the organic solutes
                solute_names = new string[3] { "urea", "nh4", "no3" };
            }

            NewSoluteType SoluteData = new NewSoluteType();
            SoluteData.solutes = solute_names;

            new_solute.Invoke(SoluteData);
        }
    }

    /// <summary>
    /// Store today's initial N amounts
    /// </summary>
    private void StoreStatus()
    {
        for (int layer = 0; layer < dlayer.Length; layer++)
        {
            // store these values so they may be used to compute daily deltas
            TodaysInitialNH4[layer] = _nh4[layer];
            TodaysInitialNO3[layer] = _no3[layer];
        }
        TodaysInitialN = SumDoubleArray(nit_tot);
        TodaysInitialC = SumDoubleArray(carbon_tot);
    }

    /// <summary>
    /// Calculates variations in C an N, and publishes MassFlows to APSIM
    /// </summary>
    private void SendDeltaState()
    {

        double dltN = SumDoubleArray(nit_tot) - TodaysInitialN;
        double dltC = SumDoubleArray(carbon_tot) - TodaysInitialC;

        SendExternalMassFlowN(dltN);
        SendExternalMassFlowC(dltC);
    }

    /// <summary>
    /// Writes in the summaryfile a report about setup and status of SoilNitrogen
    /// </summary>
    private void WriteSummaryReport()
    {

        string myMessage = "";
        Console.WriteLine();
        if (usingSimpleSoilTemp)
            myMessage = "   - Soil temperature calculated internally";
        else
            myMessage = "   - Soil temperature supplied by apsim";
        Console.WriteLine("        " + myMessage);
        if (usingSimpleSoilpH)
            myMessage = "   - Soil pH was not supplied, default value will be used";
        else
            myMessage = "   - Soil pH supplied by apsim";
        Console.WriteLine("        " + myMessage);
        Console.WriteLine();

        Console.Write(@"
                      Soil Profile Properties
          ------------------------------------------------
           Layer    pH    OC     NO3     NH4    Urea
                         (%) (kg/ha) (kg/ha) (kg/ha)
          ------------------------------------------------
");
        for (int layer = 0; layer < dlayer.Length; ++layer)
        {
            Console.WriteLine("          {0,4:d1}     {1,4:F2}  {2,4:F2}  {3,6:F2}  {4,6:F2}  {5,6:F2}",
            layer + 1, ph[layer], oc[layer], no3[layer], nh4[layer], urea[layer]);
        }
        Console.WriteLine("          ------------------------------------------------");
        Console.WriteLine("           Totals              {0,6:F2}  {1,6:F2}  {2,6:F2}",
                  SumDoubleArray(no3), SumDoubleArray(nh4), SumDoubleArray(urea));
        Console.WriteLine("          ------------------------------------------------");
        Console.WriteLine();
        Console.Write(@"
                  Initial Soil Organic Matter Status
          ---------------------------------------------------------
           Layer      Hum-C   Hum-N  Biom-C  Biom-N   FOM-C   FOM-N
                    (kg/ha) (kg/ha) (kg/ha) (kg/ha) (kg/ha) (kg/ha)
          ---------------------------------------------------------
");

        double TotalFomC = 0.0;
        for (int layer = 0; layer < dlayer.Length; ++layer)
        {
            TotalFomC += fom_c[layer];
            Console.WriteLine("          {0,4:d1}   {1,10:F1}{2,8:F1}{3,8:F1}{4,8:F1}{5,8:F1}{6,8:F1}",
            layer + 1, hum_c[layer], hum_n[layer], biom_c[layer], biom_n[layer], fom_c[layer], fom_n[layer]);
        }
        Console.WriteLine("          ---------------------------------------------------------");
        Console.WriteLine("           Totals{0,10:F1}{1,8:F1}{2,8:F1}{3,8:F1}{4,8:F1}{5,8:F1}",
            SumDoubleArray(hum_c), SumDoubleArray(hum_n), SumDoubleArray(biom_c),
            SumDoubleArray(biom_n), TotalFomC, SumDoubleArray(fom_n));
        Console.WriteLine("          ---------------------------------------------------------");
        Console.WriteLine();
    }

    #endregion setup events

    #region >>  Process events handlers and methods

    #region »   Recurrent processes (each timestep)

    /// <summary>
    /// Sets the commands for each timestep - at very beginning of of it
    /// </summary>
    /// <param name="time">The date time</param>
    [EventHandler(EventName = "tick")]
    public void OnTick(TimeType time)
    {
        if (initDone)
        {
            // store some initial values, so they may be for mass balance
            StoreStatus();
            // clear variables holding deltas
            ClearDeltaVariables();
        }
    }

    /// <summary>
    /// Sets the commands for each timestep - at the main process phase
    /// </summary>
    [EventHandler(EventName = "process")]
    public void OnProcess()
    {

        // update soil temperature
        if (usingSimpleSoilTemp)
            Tsoil = simpleST.SoilTemperature(Clock.Today, MetFile.MinT, MetFile.MaxT, MetFile.Radn, salb, dlayer, SoilDensity, ll15_dep, sw_dep);
        else
            Tsoil = ave_soil_temp;

        // calculate C and N processes
        EvaluateProcesses();

        // send actual decomposition back to surface OM
        if (!isPondActive)
        {
            PackActualResidueDecomposition();
            SendActualResidueDecompositionCalculated();
        }
    }

    #endregion recurrent processes

    #region »   Sporadic processes (not necessarily every timestep)

    /// <summary>
    /// Set the commands for writing a summary report to the summaryfile
    /// </summary>
    [EventHandler(EventName = "sum_report")]
    public void OnSum_report()
    {
        WriteSummaryReport();
    }

    /// <summary>
    /// Passes the information about the potential decomposition of surface residues
    /// </summary>
    /// <remarks>
    /// This information is passed by a residue/SurfaceOM module
    /// </remarks>
    /// <param name="SurfaceOrganicMatterDecomp">Data about the potential decomposition of each residue type on soil surface</param>
    [EventHandler(EventName = "PotentialResidueDecompositionCalculated")]
    public void OnPotentialResidueDecompositionCalculated(SurfaceOrganicMatterDecompType SurfaceOrganicMatterDecomp)
    {

        // number of residues being considered
        int nResidues = SurfaceOrganicMatterDecomp.Pool.Length;

        // zero variables by assigning new array
        residueName = new string[nResidues];
        residueType = new string[nResidues];
        pot_c_decomp = new double[nResidues];
        pot_n_decomp = new double[nResidues];
        pot_p_decomp = new double[nResidues];

        // store potential decomposition into appropriate variables
        for (int residue = 0; residue < nResidues; residue++)
        {
            residueName[residue] = SurfaceOrganicMatterDecomp.Pool[residue].Name;
            residueType[residue] = SurfaceOrganicMatterDecomp.Pool[residue].OrganicMatterType;
            pot_c_decomp[residue] = SurfaceOrganicMatterDecomp.Pool[residue].FOM.C;
            pot_n_decomp[residue] = SurfaceOrganicMatterDecomp.Pool[residue].FOM.N;
            // this P decomposition is needed to formulate data required by SOILP - struth, this is very ugly
            pot_p_decomp[residue] = SurfaceOrganicMatterDecomp.Pool[residue].FOM.P;
        }
    }

    /// <summary>
    /// Gather the information about actual residue decomposition, to be sent back to surface OM
    /// </summary>
    private void PackActualResidueDecomposition()
    {
        // Notes:
        //      Potential decomposition was given to this module by a residue/surfaceOM module.  Now we explicitly tell the
        //      module the actual decomposition rate for each of its residues. 


        int nLayers = dlayer.Length;
        soilp_dlt_org_p = new double[nLayers];

        if (actualResidueDecompositionCalculated != null && SumDoubleArray(pot_c_decomp) >= epsilon)
        {
            int nResidues = residueName.Length;     // number of residues in the simulation
            double soilp_cpr = MathUtility.Divide(SumDoubleArray(pot_p_decomp), SumDoubleArray(pot_c_decomp), 0.0);  // C:P ratio for potential decomposition

            SurfOMActualDecomposition = new SurfaceOrganicMatterDecompType();
            Array.Resize(ref SurfOMActualDecomposition.Pool, nResidues);

            for (int residue = 0; residue < nResidues; residue++)
            {
                double c_summed = SumDoubleArray(dlt_c_decomp[residue]);
                if (Math.Abs(c_summed) < epsilon)
                    c_summed = 0.0;
                double n_summed = SumDoubleArray(dlt_n_decomp[residue]);
                if (Math.Abs(n_summed) < epsilon)
                    n_summed = 0.0;

                // pack up the structure to return decompositions to SurfaceOrganicMatter
                SurfOMActualDecomposition.Pool[residue] = new SurfaceOrganicMatterDecompPoolType();
                SurfOMActualDecomposition.Pool[residue].FOM = new FOMType();
                SurfOMActualDecomposition.Pool[residue].Name = residueName[residue];
                SurfOMActualDecomposition.Pool[residue].OrganicMatterType = residueType[residue];
                SurfOMActualDecomposition.Pool[residue].FOM.amount = 0.0F;
                SurfOMActualDecomposition.Pool[residue].FOM.C = (float)c_summed;
                SurfOMActualDecomposition.Pool[residue].FOM.N = (float)n_summed;
                SurfOMActualDecomposition.Pool[residue].FOM.P = 0.0F;
                SurfOMActualDecomposition.Pool[residue].FOM.AshAlk = 0.0F;
                // Note: The values for 'amount', 'P', and 'AshAlk' will not be collected by SurfaceOrganicMatter, so send zero as default.
            }
            // dsg 131004  calculate the old dlt_org_p (from the old Decomposed event sent by residue2) for getting by soilp
            double act_c_decomp = 0.0;
            double tot_pot_c_decomp = SumDoubleArray(pot_c_decomp);
            double tot_pot_p_decomp = SumDoubleArray(pot_p_decomp);
            for (int layer = 0; layer < nLayers; layer++)
            {
                act_c_decomp = dlt_c_res_to_biom[layer] + dlt_c_res_to_hum[layer] + dlt_c_res_to_atm[layer];
                soilp_dlt_org_p[layer] = tot_pot_p_decomp * MathUtility.Divide(act_c_decomp, tot_pot_c_decomp, 0.0);
            }
        }
    }

    /// <summary>
    /// Sends back to SurfaceOM the information about residue decomposition
    /// </summary>
    private void SendActualResidueDecompositionCalculated()
    {
        // Note:
        //      - Potential decomposition was given to this module by a residue/surfaceOM module. This module evaluated
        //          whether the conditions (C-N balance) allowed the decomposition to happen.
        //		- Now we explicitly tell the sender module the actual decomposition rate for each of its residues.
        //      - If there wasn't enough mineral N to decompose, the rate will be reduced to zero !!  - MUST CHECK THE VALIDITY OF THIS

        if (actualResidueDecompositionCalculated != null && SumDoubleArray(pot_c_decomp) >= epsilon)
        {
            int nLayers = dlayer.Length;
            int nResidues = residueName.Length;
            SurfaceOrganicMatterDecompType SurfOMDecomposed = new SurfaceOrganicMatterDecompType();
            SurfOMDecomposed.Pool = new SurfaceOrganicMatterDecompPoolType[nResidues];

            double[] c_summed_layer = new double[nLayers];

            for (int residue = 0; residue < nResidues; residue++)
            {
                double c_summed = SurfOMActualDecomposition.Pool[residue].FOM.C;
                double n_summed = SurfOMActualDecomposition.Pool[residue].FOM.N;

                // pack up the structure to return decompositions to SurfaceOrganicMatter
                SurfOMDecomposed.Pool[residue] = new SurfaceOrganicMatterDecompPoolType();
                SurfOMDecomposed.Pool[residue].FOM = new FOMType();
                SurfOMDecomposed.Pool[residue].Name = SurfOMActualDecomposition.Pool[residue].Name;
                SurfOMDecomposed.Pool[residue].OrganicMatterType = SurfOMActualDecomposition.Pool[residue].OrganicMatterType;
                SurfOMDecomposed.Pool[residue].FOM.amount = 0.0F;
                SurfOMDecomposed.Pool[residue].FOM.C = (float)c_summed;
                SurfOMDecomposed.Pool[residue].FOM.N = (float)n_summed;
                SurfOMDecomposed.Pool[residue].FOM.P = 0.0F;
                SurfOMDecomposed.Pool[residue].FOM.AshAlk = 0.0F;
                // Note: The values for 'amount', 'P', and 'AshAlk' will not be collected by SurfaceOrganicMatter, so send zero as default.

            }
            // raise the event
            actualResidueDecompositionCalculated.Invoke(SurfOMDecomposed);
        }
    }

    /// <summary>
    /// Passes the instructions to incorporate FOM to the soil - simple FOM
    /// </summary>
    /// <remarks>
    /// In this event, the FOM is given as a single amount, so it will be assumed that the CN ratios of all fractions are equal
    /// Notes:
    ///     - if N (or CNR) values are not given, no FOM will not be added to soil
    ///     - if both CNR and N values are given, CNR is used and N is overwritten
    /// </remarks>
    /// <param name="inFOMdata">Data about the FOM to be added to the soil</param>
    [EventHandler(EventName = "IncorpFOM")]
    public void OnIncorpFOM(FOMLayerType inFOMdata)
    {
        int nLayers = dlayer.Length;

        // get the total amount to be added
        double totalCAmount = 0.0;
        double totalNAmount = 0.0;
        double amountCnotAdded = 0.0;
        double amountNnotAdded = 0.0;
        for (int layer = 0; layer < inFOMdata.Layer.Length; layer++)
        {
            if (layer < nLayers)
            {
                if (inFOMdata.Layer[layer].FOM.amount >= epsilon)
                {
                    inFOMdata.Layer[layer].FOM.C = inFOMdata.Layer[layer].FOM.amount * (float)defaultCarbonInFOM;
                    if (inFOMdata.Layer[layer].CNR > epsilon)
                    {   // we have C:N info - note that this has precedence over N amount
                        totalCAmount += inFOMdata.Layer[layer].FOM.C;
                        inFOMdata.Layer[layer].FOM.N = inFOMdata.Layer[layer].FOM.C / inFOMdata.Layer[layer].CNR;
                        totalNAmount += inFOMdata.Layer[layer].FOM.N;
                    }
                    else if (inFOMdata.Layer[layer].FOM.N > epsilon)
                    {   // we have N info
                        totalCAmount += inFOMdata.Layer[layer].FOM.C;
                        totalNAmount += inFOMdata.Layer[layer].FOM.N;
                    }
                    else
                    {   // no info for N
                        amountCnotAdded += inFOMdata.Layer[layer].FOM.C;
                    }
                }
                else if (inFOMdata.Layer[layer].FOM.N >= epsilon)
                {   // no info for C
                    amountNnotAdded += inFOMdata.Layer[layer].FOM.N;
                }
            }
            else
                Console.WriteLine(" IncorpFOM: information passed contained more layers than the soil, these will be ignored");
        }

        // If any FOM was passed, make the partition into FOM pools
        if (totalCAmount >= epsilon)
        {
            // check whether a valid FOM type was given
            fom_type = 0;   // use the default if fom_type was not given
            for (int i = 0; i < fom_types.Length; i++)
            {
                if (inFOMdata.Type == fom_types[i])
                {
                    fom_type = i;
                    break;
                }
            }

            // Pack the handled values of fom, so they can be added to the soil
            FOMPoolType myFOMPoolData = new FOMPoolType();
            myFOMPoolData.Layer = new FOMPoolLayerType[inFOMdata.Layer.Length];

            // Now partition the .C and .N amounts into FOM pools
            for (int layer = 0; layer < inFOMdata.Layer.Length; layer++)
            {
                if (layer < nLayers)
                {
                    myFOMPoolData.Layer[layer] = new FOMPoolLayerType();
                    myFOMPoolData.Layer[layer].Pool = new FOMType[3];
                    myFOMPoolData.Layer[layer].Pool[0] = new FOMType();
                    myFOMPoolData.Layer[layer].Pool[1] = new FOMType();
                    myFOMPoolData.Layer[layer].Pool[2] = new FOMType();

                    if (inFOMdata.Layer[layer].FOM.C > epsilon)
                    {
                        myFOMPoolData.Layer[layer].nh4 = 0.0F;
                        myFOMPoolData.Layer[layer].no3 = 0.0F;
                        myFOMPoolData.Layer[layer].Pool[0].C = (float)(inFOMdata.Layer[layer].FOM.C * fract_carb[fom_type]);
                        myFOMPoolData.Layer[layer].Pool[1].C = (float)(inFOMdata.Layer[layer].FOM.C * fract_cell[fom_type]);
                        myFOMPoolData.Layer[layer].Pool[2].C = (float)(inFOMdata.Layer[layer].FOM.C * fract_lign[fom_type]);

                        myFOMPoolData.Layer[layer].Pool[0].N = (float)(inFOMdata.Layer[layer].FOM.N * fract_carb[fom_type]);
                        myFOMPoolData.Layer[layer].Pool[1].N = (float)(inFOMdata.Layer[layer].FOM.N * fract_cell[fom_type]);
                        myFOMPoolData.Layer[layer].Pool[2].N = (float)(inFOMdata.Layer[layer].FOM.N * fract_lign[fom_type]);
                    }
                }
            }

            // actually add the FOM to soil
            IncorporateFOM(myFOMPoolData);
        }
        else
            writeMessage("IncorpFOM: action was not carried out because no amount was given");

        // let the user know of any issues
        if ((amountCnotAdded >= epsilon) | (amountNnotAdded >= epsilon))
        {
            writeMessage("IncorpFOM - Warning Error: The amounts of " + amountCnotAdded.ToString("#0.00") +
            "kgC/ha and " + amountCnotAdded.ToString("#0.00") + "were not added because some information was missing");
        }
    }

    /// <summary>
    /// Passes the instructions to incorporate FOM to the soil - FOM pools
    /// </summary>
    /// <remarks>
    /// In this event, the FOM amount is given already partitioned by pool
    /// </remarks>
    /// <param name="inFOMPoolData">Data about the FOM to be added to the soil</param>
    [EventHandler(EventName = "IncorpFOMPool")]
    public void OnIncorpFOMPool(FOMPoolType inFOMPoolData)
    {
        // get the total amount to be added
        double totalCAmount = 0.0;
        double totalNAmount = 0.0;
        double amountCnotAdded = 0.0;
        double amountNnotAdded = 0.0;
        for (int layer = 0; layer < inFOMPoolData.Layer.Length; layer++)
        {
            if (layer < dlayer.Length)
            {
                for (int pool = 0; pool < 3; pool++)
                {
                    int teste1 = inFOMPoolData.Layer.Rank;
                    int teste2 = inFOMPoolData.Layer.Length;
                    int teste3 = inFOMPoolData.Layer.GetLength(0);
                    if (inFOMPoolData.Layer[layer].Pool[pool].C >= epsilon)
                    {   // we have both C and N, can add
                        totalCAmount += inFOMPoolData.Layer[layer].Pool[pool].C;
                        totalNAmount += inFOMPoolData.Layer[layer].Pool[pool].N;
                    }
                    else
                    {   // some data is mising, cannot add
                        amountCnotAdded += inFOMPoolData.Layer[layer].Pool[pool].C;
                        amountNnotAdded += inFOMPoolData.Layer[layer].Pool[pool].N;
                    }
                }
            }
            else
                Console.WriteLine(" IncorpFOMPool: information passed contained more layers than the soil, these will be ignored");
        }

        // add FOM to soil layers, if given
        if (totalCAmount >= epsilon)
            IncorporateFOM(inFOMPoolData);
        else
            writeMessage("IncorpFOMPool: action was not carried out because no amount was given");

        // let the user know of any issues
        if ((amountCnotAdded >= epsilon) | (amountNnotAdded >= epsilon))
        {
            writeMessage("IncorpFOMPool - Warning Error: The amounts of " + amountCnotAdded.ToString("#0.00") +
            "kgC/ha and " + amountCnotAdded.ToString("#0.00") + "were not added because some information was missing");
        }
    }

    /// <summary>
    /// Gets the data about incoming FOM, add to the patch's FOM pools
    /// </summary>
    /// <remarks>
    /// The FOM amount is given already partitioned by pool
    /// </remarks>
    /// <param name="FOMPoolData"></param>
    public void IncorporateFOM(FOMPoolType FOMPoolData)
    {
        for (int layer = 0; layer < FOMPoolData.Layer.Length; layer++)
        {
            // update FOM amounts and check values
            for (int pool = 0; pool < 3; pool++)
            {
                fom_c_pool[pool][layer] += FOMPoolData.Layer[layer].Pool[pool].C;
                fom_n_pool[pool][layer] += FOMPoolData.Layer[layer].Pool[pool].N;
                CheckNegativeValues(ref fom_c_pool[pool][layer], layer, "fom_c_pool" + (pool + 1).ToString(), "IncorporateFOM");
                CheckNegativeValues(ref fom_n_pool[pool][layer], layer, "fom_n_pool" + (pool + 1).ToString(), "IncorporateFOM");
            }
            // update mineral N forms and check values
            _nh4[layer] += FOMPoolData.Layer[layer].nh4;
            _no3[layer] += FOMPoolData.Layer[layer].no3;
            CheckNegativeValues(ref _nh4[layer], layer, "nh4", "IncorporateFOM");
            CheckNegativeValues(ref _no3[layer], layer, "no3", "IncorporateFOM");
        }
    }

    /// <summary>
    /// Passes the information about setting/changes in the soil profile
    /// </summary>
    /// <remarks>
    /// We get the basic soil physics data in here;
    /// The event is also used to account for getting changes due to soil erosion
    /// It is assumed that if there are any changes in the soil profile the module doing it will let us know.
    ///     this will be done by setting both 'soil_loss' and 'n_reduction' (ProfileReductionAllowed) to a non-default value
    /// </remarks>
    /// <param name="NewProfile">Data about the new soil profile</param>
    [EventHandler(EventName = "new_profile")]
    public void OnNew_profile(NewProfileType NewProfile)
    {
        // get the basic soil data info
        int nLayers = NewProfile.dlayer.Length;
        Array.Resize(ref SoilDensity, nLayers);
        Array.Resize(ref ll15_dep, nLayers);
        Array.Resize(ref dul_dep, nLayers);
        Array.Resize(ref sat_dep, nLayers);
        for (int layer = 0; layer < nLayers; layer++)
        {
            SoilDensity[layer] = (double)NewProfile.bd[layer];
            ll15_dep[layer] = (double)NewProfile.ll15_dep[layer];
            dul_dep[layer] = (double)NewProfile.dul_dep[layer];
            sat_dep[layer] = (double)NewProfile.sat_dep[layer];
        }

        // check the layer structure
        if (dlayer == null)
        {
            // we are at initialisation, set the layer structure
            Array.Resize(ref dlayer, nLayers);
            Array.Resize(ref reset_dlayer, nLayers);
            for (int layer = 0; layer < nLayers; layer++)
            {
                dlayer[layer] = (double)NewProfile.dlayer[layer];
                reset_dlayer[layer] = dlayer[layer];
            }
        }
        else if (soil_loss > epsilon && ProfileReductionAllowed)
        {
            // check for variations in the soil profile. and update the C and N amounts appropriately
            // Are these changes mainly (only) due to by erosion? what else??
            double[] new_dlayer = new double[NewProfile.dlayer.Length];
            for (int layer = 0; layer < new_dlayer.Length; layer++)
                new_dlayer[layer] = (double)NewProfile.dlayer[layer];

            // RCichota: this routine will need carefull review, what happens if soil profile is changed and then we have a reset?
            //  I have set up a 'reset_dlayer' which holds the original dlayer and may be used check the changes in the soil
            //   profile. But what about the other variables??

            // There have been some soil loss, launch the UpdateProfile
            UpdateProfile(new_dlayer);

            // don't we need to update the other variables (at least their size)???
        }
    }

    /// <summary>
    /// Check whether profile has changed and move values between layers
    /// </summary>
    /// <param name="new_dlayer">New values for dlayer</param>
    private void UpdateProfile(double[] new_dlayer)
    {
        dlt_n_loss_in_sed = 0.0;
        dlt_c_loss_in_sed = 0.0;

        // How to decide:
        // If bedrock is lower than lowest  profile depth, we won't see any change in profile (layer structure),
        //  even if there is erosion. If bedrock is reached, than the profile is actually reduced.

        // Ideally we should test both soil_loss and dlayer for changes to cater for
        // manager control. But, the latter means we have to fudge enr for the loss from top layer.

        // move pools
        // EJZ: Why aren't no3 and urea moved????  - RCichota: added urea and no3.  What happens with this when there is reset???
        dlt_n_loss_in_sed += MoveLayers(ref _urea, new_dlayer);
        dlt_n_loss_in_sed += MoveLayers(ref _nh4, new_dlayer);
        dlt_n_loss_in_sed += MoveLayers(ref _no3, new_dlayer);
        dlt_c_loss_in_sed += MoveLayers(ref inert_c, new_dlayer);
        dlt_c_loss_in_sed += MoveLayers(ref biom_c, new_dlayer);
        dlt_n_loss_in_sed += MoveLayers(ref biom_n, new_dlayer);
        dlt_c_loss_in_sed += MoveLayers(ref hum_c, new_dlayer);
        dlt_n_loss_in_sed += MoveLayers(ref hum_n, new_dlayer);
        for (int pool = 0; pool < 3; pool++)
        {
            dlt_n_loss_in_sed += MoveLayers(ref fom_n_pool[pool], new_dlayer);
            dlt_c_loss_in_sed += MoveLayers(ref fom_c_pool[pool], new_dlayer);
        }
        // now update the layer structure
        for (int layer = 0; layer < new_dlayer.Length; layer++)
            dlayer[layer] = new_dlayer[layer];
    }

    /// <summary>
    /// Move the values of a given variable between layers, from bottom to top
    /// </summary>
    /// <remarks>
    /// Used when there is some soil lost due to erosion
    ///     // Changed from subroutine to function returning amount of profile loss
    /// </remarks>
    /// <param name="SoilProperty">Variable to move layers</param>
    /// <param name="new_dlayer">new dlayer array</param>
    /// <returns>Amount of C or N lost because of changes in profile</returns>
    private double MoveLayers(ref double[] SoilProperty, double[] new_dlayer)
    {
        double layer_loss = 0.0;
        double layer_gain = 0.0;
        int lowest_layer = dlayer.Length;
        int lowestLayer = dlayer.Length - 1;
        int new_lowest_layer = new_dlayer.Length;

        double TodaysInitialAmount = SumDoubleArray(SoilProperty);
        double SoilLossThickness = soil_loss / (SoilDensity[0] * 10);  //  10 = 1000/10000, converts loss to kg/m2, then to L/m2 = mm
        double AmountGainedBottom = 0.0;
        double AmountLostTop = 0.0;

        // check whether soil loss is not bigger than any layer
        for (int layer = 0; layer < lowestLayer; layer++)
        {
            SoilLossThickness = soil_loss * (SoilDensity[layer] * 10);
            if (dlayer[layer] < SoilLossThickness)
            {
                double LayerDeviation = ((SoilLossThickness / dlayer[layer]) - 1) * 100;
                throw new Exception("Soil loss is greater than the thickness of layer(" + layer.ToString() + "), by " + LayerDeviation.ToString("#0.00") +
                    "%.\nConstrained to this layer. Re-mapping of SoilN pools will be incorrect.");
            }
        }

        // initialise layer loss from below profile same as bottom layer
        if (Math.Abs(SumDoubleArray(dlayer) - SumDoubleArray(new_dlayer)) < epsilon)
        {
            // Soil profile structure did not change, update values among layers
            // Assuming soil was lost in the surface, move values from the bottom up (assumes the properties of the soil below our profile are the same as the bottom layer)
            AmountGainedBottom = SoilProperty[lowestLayer] * Math.Min(1.0, SoilLossThickness / dlayer[lowestLayer]);
        }
        else if (SumDoubleArray(dlayer) < SumDoubleArray(new_dlayer))
        {
            // soil profile has been increased (assume material has been added to the surface
            AmountGainedBottom = 0.0;
            // check for changes in layer structure
            if (dlayer.Length < new_dlayer.Length)
            {
                throw new Exception("Number of soil layers was increased, this option has not been coded in SoilNitrogen");
            }
        }
        else
        {
            // soil profile has been reduced
            AmountGainedBottom = 0.0;

            // check for changes in layer structure
            if (new_dlayer.Length < dlayer.Length)
            {
                // we have less layers than before.  The layer structure is kept, from top down, thus in fact the bottom layer merges with the second bottom etc.

                // merge the bottom layers
                for (int layer = lowestLayer; layer >= new_dlayer.Length; layer--)
                {
                    SoilProperty[layer - 1] += SoilProperty[layer];
                }
                Array.Resize(ref SoilProperty, new_dlayer.Length);  // isn't this also done later on? no, but I think it should (do a ResizeArrays)
            }
            else
            {
                // the thickness of some layer(s) changed 
                throw new Exception("Soil loss has reduced soil profile, but did not change number of layers, this option has not been coded in SoilNitrogen");
            }
        }

        layer_gain = AmountGainedBottom;

        // now move from bottom layer to top
        for (int layer = new_lowest_layer - 1; layer >= 0; layer--)
        {
            // this layer gains what the lower layer lost
            SoilLossThickness = soil_loss * (SoilDensity[layer] * 10);
            layer_loss = SoilProperty[layer] * Math.Min(1.0, SoilLossThickness / dlayer[layer]);
            SoilProperty[layer] += layer_gain - layer_loss;
            layer_gain = layer_loss;
        }

        // now adjust top layer using enrichment equation
        double enr = enr_a_coeff * Math.Pow(soil_loss * 1000, -1.0 * enr_b_coeff);
        enr = Math.Max(1.0, Math.Min(enr, enr_a_coeff));

        AmountLostTop = layer_loss * enr;
        SoilProperty[0] = Math.Max(0.0, SoilProperty[0] + layer_loss - AmountLostTop);

        // check mass balance
        double UpdatedAmount = SumDoubleArray(SoilProperty);
        TodaysInitialAmount += AmountGainedBottom - AmountLostTop;
        if (!MathUtility.FloatsAreEqual(UpdatedAmount, TodaysInitialAmount))
        {
            throw new Exception("N mass balance out");
        }
        // why don't we test AmountLostTop against Soil_lost??
        return AmountLostTop;
    }

    /// <summary>
    /// Passes the information about changes in mineral N made by other modules
    /// </summary>
    /// <param name="NitrogenChanges">The variation (delta) for each mineral N form</param>
    [EventHandler(EventName = "NitrogenChanged")]
    public void OnNitrogenChanged(NitrogenChangedType NitrogenChanges)
    {
        // check whether there are significant values, if so pass them to appropriate dlt
        if (hasSignificantValues(NitrogenChanges.DeltaUrea, epsilon))
            dlt_urea = NitrogenChanges.DeltaUrea;
        if (hasSignificantValues(NitrogenChanges.DeltaNH4, epsilon))
            dlt_nh4 = NitrogenChanges.DeltaNH4;
        if (hasSignificantValues(NitrogenChanges.DeltaNO3, epsilon))
            dlt_no3 = NitrogenChanges.DeltaNO3;
    }

    /// <summary>
    /// Get the information about urine being added
    /// </summary>
    /// <param name="UrineAdded">Urine deposition data (includes urea N amount, volume, area affected, etc)</param>
    [EventHandler(EventName = "AddUrine")]
    public void OnAddUrine(AddUrineType UrineAdded)
    {
        // Starting with the minimalist version. To be updated by Val's group to include a urine patch algorithm
        _urea[0] += UrineAdded.Urea;
    }

    /// <summary>
    /// Comunicate other components that C amount in the soil has changed
    /// </summary>
    /// <param name="dltC">C changes</param>
    private void SendExternalMassFlowC(double dltC)
    {
        ExternalMassFlowType massBalanceChange = new ExternalMassFlowType();
        if (Math.Abs(dltC) < epsilon)
            dltC = 0.0;     // don't bother with values that are too small
        massBalanceChange.FlowType = dltC >= epsilon ? "gain" : "loss";
        massBalanceChange.PoolClass = "soil";
        massBalanceChange.N = (float)Math.Abs(dltC);
        ExternalMassFlow.Invoke(massBalanceChange);
    }

    /// <summary>
    /// Comunicate other components that N amount in the soil has changed
    /// </summary>
    /// <param name="dltN">N changes</param>
    private void SendExternalMassFlowN(double dltN)
    {
        ExternalMassFlowType massBalanceChange = new ExternalMassFlowType();
        if (Math.Abs(dltN) < epsilon)
            dltN = 0.0;
        massBalanceChange.FlowType = dltN >= epsilon ? "gain" : "loss";
        massBalanceChange.PoolClass = "soil";
        massBalanceChange.N = (float)Math.Abs(dltN);
        ExternalMassFlow.Invoke(massBalanceChange);
    }

    #endregion sporadic processes

    #endregion processes events

    #region >>  Auxiliary functions

    /// <summary>
    /// Print a message to the summaryfile
    /// </summary>
    /// <param name="myMessage">The message to be printed below the date and module info</param>
    private void writeMessage(string myMessage)
    {
        Console.WriteLine(Clock.Today.ToString("dd MMMM yyyy") + " (Day of year=" + Clock.Today.DayOfYear.ToString() + "), SoilNitrogen:");
        Console.WriteLine("     " + myMessage);
    }

    /// <summary>
    /// Checks whether the variable is significantly negative, considering thresholds
    /// </summary>
    /// <remarks>
    /// Three levels are considered when analying a negative value, these are defined by the warning and the fatal threshold value:
    ///  (1) If the variable is negative, but the value is really small (in absolute terms) than the deviation is considered irrelevant;
    ///  (2) If the value of the variable is negative and greater than the warning threshold, then a warning message is given;
    ///  (3) If the variable value is negative and greater than the fatal threshold, then a fatal error is raised and the calculation stops.
    /// In any case the value any negative value is reset to zero;
    /// </remarks>
    /// <param name="TheVariable">Reference to the variable being tested</param>
    /// <param name="layer">The layer to which the variable belongs to</param>
    /// <param name="VariableName">The name of the variable</param>
    /// <param name="MethodName">The name of the method calling the test</param>
    private void CheckNegativeValues(ref double TheValue, int layer, string VariableName, string MethodName)
    {
        // Note: the layer number and the variable name are passed only so that they can be added to the error message

        if (TheValue < FatalNegativeThreshold)
        {
            // Deviation is too large, stop the calculations
            TheValue = 0.0;
            throw new Exception(" - " + MethodName + ", attempt to change " + VariableName + "[" + (layer + 1).ToString() +
                "] to a value below the fatal threshold, " +
                FatalNegativeThreshold.ToString());
        }
        else if (TheValue < WarningNegativeThreshold)
        {
            // Deviation is small, but warrants a notice to user
            TheValue = 0.0;
            string myMessage = " - " + MethodName + ", attempt to change " + VariableName + "[" +
                (layer + 1).ToString() + "] to a value below the warning threshold, " +
                WarningNegativeThreshold.ToString() + ". Value will be reset to zero";
            writeMessage(myMessage);
        }
        else if (TheValue < 0.0)
        {
            // Realy small value, likely a numeric issue, don't bother to report
            TheValue = 0.0;
        }
        //else { } // Value is positive
    }

    /// <summary>
    /// Computes the fraction of each layer that is between the surface and a given depth
    /// </summary>
    /// <param name="maxDepth">The depth down to which the fractions are computed</param>
    /// <returns>An array with the fraction (0-1) of each layer that is between the surface and maxDepth</returns>
    private double[] FractionLayer(double maxDepth)
    {
        double cumDepth = 0.0;
        double[] result = new double[dlayer.Length];
        int maxLayer = getCumulativeIndex(maxDepth, dlayer);
        for (int layer = 0; layer <= maxLayer; layer++)
        {
            result[layer] = Math.Min(1.0, MathUtility.Divide(maxDepth - cumDepth, dlayer[layer], 0.0));
        }
        return result;
    }

    /// <summary>
    /// Find the index at which the cumulative amount is equal or greater than a given value
    /// </summary>
    /// <param name="sumTarget">The target value being sought</param>
    /// <param name="anArray">The array to analyse</param>
    /// <returns>The index of the array item at which the sum is equal or greater than the target</returns>
    private int getCumulativeIndex(double sumTarget, double[] anArray)
    {
        double cum = 0.0f;
        for (int i = 0; i < anArray.Length; i++)
        {
            cum += anArray[i];
            if (cum >= sumTarget)
                return i;
        }
        return anArray.Length - 1;
    }

    /// <summary>
    /// Check whether there is at least one considerable/significant value in the array
    /// </summary>
    /// <param name="anArray">The array to analyse</param>
    /// <param name="MinValue">The minimum considerable value</param>
    /// <returns>True if there is any value greater than the minimum, false otherwise</returns>
    private bool hasSignificantValues(double[] anArray, double MinValue)
    {
        bool result = false;
        if (anArray != null)
        {
            foreach (double Value in anArray)
            {
                if (Math.Abs(Value) >= MinValue)
                {
                    result = true;
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Calculate the sum of all values of an array of doubles
    /// </summary>
    /// <param name="anArray">The array of values</param>
    /// <returns>The sum</returns>
    private double SumDoubleArray(double[] anArray)
    {
        double result = 0.0;
        if (anArray != null)
        {
            foreach (double Value in anArray)
                result += Value;
        }
        return result;
    }

    #endregion Aux functions
}

public class SoilTypeDefinition
{
    [Param]
    protected XmlNode SoilTypeDefinitionXML;
}
