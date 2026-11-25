// Words used to clean up handwriting readings: the lecture's own words plus a built-in medical list.

using System.Text.RegularExpressions;

namespace ConvertToMarkdown;

class Vocabulary
{
    static readonly string[] Medical = (
        // kidney / urinary
        "kidney kidneys renal nephron nephrons glomerulus glomeruli glomerular glomerulonephritis nephritis nephrotic nephritic " +
        "nephropathy nephrology nephrologist urine urinary urinalysis urethra urethral ureter ureters bladder proteinuria " +
        "hematuria haematuria oliguria anuria polyuria nocturia dysuria enuresis albumin albuminuria creatinine urea uremia " +
        "uremic azotemia dialysis hemodialysis peritoneal transplant transplantation filtration reabsorption clearance " +
        "insufficiency failure chronic acute injury obstruction obstructive hydronephrosis reflux vesicoureteral posterior " +
        "valves valve dysplasia hypoplasia polycystic cystic stone stones calculi nephrolithiasis prerenal postrenal intrinsic " +
        "tubular tubule tubules necrosis interstitial hemolytic syndrome erythropoietin osteodystrophy rickets " +
        // general medicine
        "congenital acquired autoimmune immune infection infections infectious inflammation inflammatory sepsis septic " +
        "dehydration hypovolemia hypovolemic bleeding hemorrhage haemorrhage perfusion reperfusion ischemia ischaemia " +
        "shock hypotension hypertension hypertensive edema oedema ascites effusion anemia anaemia normocytic microcytic " +
        "macrocytic petechiae purpura pallor platelet platelets thrombocytopenia leukocytosis neutrophil lymphocyte " +
        "hemoglobin haemoglobin hematocrit electrolyte electrolytes sodium potassium calcium phosphate phosphorus magnesium " +
        "bicarbonate acidosis alkalosis metabolic respiratory hyperkalemia hypokalemia hypernatremia hyponatremia " +
        "hypocalcemia hypercalcemia hyperphosphatemia fluid fluids diuretic diuretics furosemide steroid steroids " +
        "prednisolone antibiotic antibiotics toxic toxin toxins nephrotoxic drugs medication dose doses headache headaches " +
        "vomiting diarrhea diarrhoea nausea fever fatigue lethargy seizure seizures convulsion encephalopathy pruritus " +
        "growth retardation delay failure thrive weight height vitamin deficiency endocrine endocrinal hormone hormonal " +
        "parathyroid thyroid insulin diabetes diabetic tumor tumour tumors malignancy carcinoma lymphoma leukemia " +
        "biopsy ultrasound imaging scan diagnosis diagnostic differential management treatment therapy prognosis " +
        "complication complications symptoms symptom signs sign syndrome disease diseases disorder pediatric paediatric " +
        "children child infant infants neonate neonatal newborn adolescent normal abnormal mild moderate severe " +
        "persistent recurrent reversible irreversible progressive stage stages early late months weeks years " +
        "systemic lupus vasculitis purpura henoch streptococcal glomerular cardiac heart failure pulmonary " +
        "cardiovascular stroke coma blood pressure serum plasma level levels high low increased decreased"
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // everyday words that should never be "corrected"
    static readonly HashSet<string> Common = new((
        "about after again also because before being between both come could does done down each even every from give " +
        "good have here into just know like long make many more most much must need never next only other over same " +
        "should show some such take than that then them there these they thing this those time very want well were " +
        "what when where which while will with without would your yes note notes page see also less more none only"
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    readonly HashSet<string> words = new();
    readonly List<string> list;

    public Vocabulary(IEnumerable<string> lectureText)
    {
        foreach (var w in Medical) words.Add(w);
        foreach (var t in lectureText)
            foreach (Match m in Regex.Matches(t, "[A-Za-z]{4,}"))
                words.Add(m.Value.ToLowerInvariant());
        list = words.ToList();
    }

    public static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z]", "");

    public bool Knows(string word)
    {
        string k = Key(word);
        return k.Length < 4 || words.Contains(k) || Common.Contains(k);
    }

    /// <summary>"Endena → edema?" style guesses for words the lecture does not contain.</summary>
    public List<string> Suggestions(string text)
    {
        var outp = new List<string>();
        var seen = new HashSet<string>();
        foreach (Match m in Regex.Matches(text, "[A-Za-z]{4,}"))
        {
            string k = m.Value.ToLowerInvariant();
            if (Knows(k) || !seen.Add(k)) continue;
            var best = list
                .Select(w => (w, r: Ratio(k, w)))
                .Where(x => x.r >= 0.8 || (x.r >= 0.66 && x.w[0] == k[0]))
                .OrderByDescending(x => x.r).Take(2).Select(x => x.w).ToList();
            if (best.Count > 0)
                outp.Add($"{m.Value} → {string.Join(" / ", best)}?");
        }
        return outp;
    }

    static double Ratio(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > Math.Max(a.Length, b.Length) / 2) return 0;
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return 1.0 - (double)d[b.Length] / Math.Max(a.Length, b.Length);
    }
}
