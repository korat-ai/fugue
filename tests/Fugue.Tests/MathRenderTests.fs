module Fugue.Tests.MathRenderTests

open Xunit
open FsUnit.Xunit
open Fugue.Cli

// ---------------------------------------------------------------------------
// Greek letters
// ---------------------------------------------------------------------------

[<Fact>]
let ``alpha replaced`` () =
    MathRender.preprocess @"$\alpha$" |> should equal "α"

[<Fact>]
let ``beta replaced`` () =
    MathRender.preprocess @"$\beta$" |> should equal "β"

[<Fact>]
let ``gamma replaced`` () =
    MathRender.preprocess @"$\gamma$" |> should equal "γ"

[<Fact>]
let ``delta replaced`` () =
    MathRender.preprocess @"$\delta$" |> should equal "δ"

[<Fact>]
let ``epsilon replaced`` () =
    MathRender.preprocess @"$\epsilon$" |> should equal "ε"

[<Fact>]
let ``pi replaced`` () =
    MathRender.preprocess @"$\pi$" |> should equal "π"

[<Fact>]
let ``sigma replaced`` () =
    MathRender.preprocess @"$\sigma$" |> should equal "σ"

[<Fact>]
let ``theta replaced`` () =
    MathRender.preprocess @"$\theta$" |> should equal "θ"

[<Fact>]
let ``lambda replaced`` () =
    MathRender.preprocess @"$\lambda$" |> should equal "λ"

[<Fact>]
let ``mu replaced`` () =
    MathRender.preprocess @"$\mu$" |> should equal "μ"

[<Fact>]
let ``phi replaced`` () =
    MathRender.preprocess @"$\phi$" |> should equal "φ"

[<Fact>]
let ``psi replaced`` () =
    MathRender.preprocess @"$\psi$" |> should equal "ψ"

[<Fact>]
let ``omega replaced`` () =
    MathRender.preprocess @"$\omega$" |> should equal "ω"

[<Fact>]
let ``uppercase Alpha replaced`` () =
    MathRender.preprocess @"$\Alpha$" |> should equal "Α"

[<Fact>]
let ``uppercase Pi replaced`` () =
    MathRender.preprocess @"$\Pi$" |> should equal "Π"

[<Fact>]
let ``uppercase Sigma replaced`` () =
    MathRender.preprocess @"$\Sigma$" |> should equal "Σ"

[<Fact>]
let ``uppercase Omega replaced`` () =
    MathRender.preprocess @"$\Omega$" |> should equal "Ω"

// ---------------------------------------------------------------------------
// Operators
// ---------------------------------------------------------------------------

[<Fact>]
let ``times replaced`` () =
    MathRender.preprocess @"$\times$" |> should equal "×"

[<Fact>]
let ``div replaced`` () =
    MathRender.preprocess @"$\div$" |> should equal "÷"

[<Fact>]
let ``pm replaced`` () =
    MathRender.preprocess @"$\pm$" |> should equal "±"

[<Fact>]
let ``leq replaced`` () =
    MathRender.preprocess @"$\leq$" |> should equal "≤"

[<Fact>]
let ``geq replaced`` () =
    MathRender.preprocess @"$\geq$" |> should equal "≥"

[<Fact>]
let ``neq replaced`` () =
    MathRender.preprocess @"$\neq$" |> should equal "≠"

[<Fact>]
let ``approx replaced`` () =
    MathRender.preprocess @"$\approx$" |> should equal "≈"

[<Fact>]
let ``infty replaced`` () =
    MathRender.preprocess @"$\infty$" |> should equal "∞"

[<Fact>]
let ``sum replaced`` () =
    MathRender.preprocess @"$\sum$" |> should equal "∑"

[<Fact>]
let ``prod replaced`` () =
    MathRender.preprocess @"$\prod$" |> should equal "∏"

[<Fact>]
let ``sqrt replaced`` () =
    MathRender.preprocess @"$\sqrt$" |> should equal "√"

[<Fact>]
let ``int replaced`` () =
    MathRender.preprocess @"$\int$" |> should equal "∫"

// ---------------------------------------------------------------------------
// Arrows
// ---------------------------------------------------------------------------

[<Fact>]
let ``to replaced`` () =
    MathRender.preprocess @"$\to$" |> should equal "→"

[<Fact>]
let ``leftarrow replaced`` () =
    MathRender.preprocess @"$\leftarrow$" |> should equal "←"

[<Fact>]
let ``Rightarrow replaced`` () =
    MathRender.preprocess @"$\Rightarrow$" |> should equal "⇒"

[<Fact>]
let ``Leftrightarrow replaced`` () =
    MathRender.preprocess @"$\Leftrightarrow$" |> should equal "⟺"

// ---------------------------------------------------------------------------
// Sets / logic
// ---------------------------------------------------------------------------

[<Fact>]
let ``in replaced`` () =
    MathRender.preprocess @"$\in$" |> should equal "∈"

[<Fact>]
let ``notin replaced`` () =
    MathRender.preprocess @"$\notin$" |> should equal "∉"

[<Fact>]
let ``subset replaced`` () =
    MathRender.preprocess @"$\subset$" |> should equal "⊂"

[<Fact>]
let ``cup replaced`` () =
    MathRender.preprocess @"$\cup$" |> should equal "∪"

[<Fact>]
let ``cap replaced`` () =
    MathRender.preprocess @"$\cap$" |> should equal "∩"

[<Fact>]
let ``emptyset replaced`` () =
    MathRender.preprocess @"$\emptyset$" |> should equal "∅"

[<Fact>]
let ``forall replaced`` () =
    MathRender.preprocess @"$\forall$" |> should equal "∀"

[<Fact>]
let ``exists replaced`` () =
    MathRender.preprocess @"$\exists$" |> should equal "∃"

// ---------------------------------------------------------------------------
// Superscripts
// ---------------------------------------------------------------------------

[<Fact>]
let ``superscript digit 2`` () =
    MathRender.preprocess @"$x^2$" |> should equal "x²"

[<Fact>]
let ``superscript digit 0`` () =
    MathRender.preprocess @"$x^0$" |> should equal "x⁰"

[<Fact>]
let ``superscript n`` () =
    MathRender.preprocess @"$x^n$" |> should equal "xⁿ"

[<Fact>]
let ``superscript i`` () =
    MathRender.preprocess @"$x^i$" |> should equal "xⁱ"

[<Fact>]
let ``superscript braced digits`` () =
    MathRender.preprocess @"$x^{10}$" |> should equal "x¹⁰"

// ---------------------------------------------------------------------------
// Subscripts
// ---------------------------------------------------------------------------

[<Fact>]
let ``subscript digit 0`` () =
    MathRender.preprocess @"$x_0$" |> should equal "x₀"

[<Fact>]
let ``subscript digit 1`` () =
    MathRender.preprocess @"$x_1$" |> should equal "x₁"

[<Fact>]
let ``subscript n`` () =
    MathRender.preprocess @"$x_n$" |> should equal "xₙ"

[<Fact>]
let ``subscript braced`` () =
    MathRender.preprocess @"$x_{00}$" |> should equal "x₀₀"

// ---------------------------------------------------------------------------
// Delimiter stripping
// ---------------------------------------------------------------------------

[<Fact>]
let ``inline dollar delimiters stripped`` () =
    MathRender.preprocess "$hello$" |> should equal "hello"

[<Fact>]
let ``display dollar delimiters stripped with spaces`` () =
    let result = MathRender.preprocess "$$hello$$"
    result.Trim() |> should equal "hello"

[<Fact>]
let ``text outside dollars is unchanged`` () =
    MathRender.preprocess "The value is $x$ units." |> should equal "The value is x units."

// ---------------------------------------------------------------------------
// \frac approximation
// ---------------------------------------------------------------------------

[<Fact>]
let ``frac simple`` () =
    MathRender.preprocess @"$\frac{1}{2}$" |> should equal "(1/2)"

[<Fact>]
let ``frac with expressions`` () =
    MathRender.preprocess @"$\frac{a+b}{c}$" |> should equal "(a+b/c)"

[<Fact>]
let ``frac nested with commands`` () =
    // Commands are replaced before frac is scanned — but frac runs first.
    // After frac: (\alpha/\beta), then command replace: (α/β), then strip $.
    MathRender.preprocess @"$\frac{\alpha}{\beta}$" |> should equal "(α/β)"

// ---------------------------------------------------------------------------
// Combined expressions
// ---------------------------------------------------------------------------

[<Fact>]
let ``combined expression E=mc^2`` () =
    let result = MathRender.preprocess @"$E = mc^2$"
    result |> should haveSubstring "mc²"

[<Fact>]
let ``combined expression sigma_i^2`` () =
    let result = MathRender.preprocess @"$\sigma_i^2$"
    result |> should haveSubstring "σ"
    result |> should haveSubstring "ᵢ"
    result |> should haveSubstring "²"

[<Fact>]
let ``plain text without math is unchanged`` () =
    let s = "Hello, world! No math here."
    MathRender.preprocess s |> should equal s

[<Fact>]
let ``empty string is unchanged`` () =
    MathRender.preprocess "" |> should equal ""
