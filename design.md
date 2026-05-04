I want to build a library that can generate SVG files to be used with a laser cutter. All measurements should be in mm.
I want to define a 3d shape - e.g. 'a 250x250x50mm box' - and have the software automatically slice it into faces and create tabs.
The software should account for kerf (laser beam thickness) by increasing the size of parts intended to fit together.
The software should allow for customising the thickness of the material and the size of the tab joints.
The software should allow the user to apply a pattern on a selected face or faces, either centered or a repeating tiled pattern. It should have a square grid as one of the default options.
The user should be able to define either the interior or exterior sizes of the shapes.

As an advanced feature, I want it to allow creating drawers and holders that can hold multiple drawers in an array - e.g. a holder that can have a 3x2 array of drawers in. This should automatically generate the files for both the drawers and the surrounding frame.

As an advanced feature, the software should be able to layout the resulting shapes. This should take a material size (e.g. 300x300 mm) and place the shapes together in as tight a packing as possible, producing one or more "pages". This should have an option for "margin" so that it can be used with machines that don't allow for exact alignment of the material.

This means we need:
* A way to define the design that the user wants. We should have a standard parseable format for this - probably YML or JSON.
* Good documentation for how the parser is used.
* A method that takes a design file and turns it into a set of 3D shape objects. This should return helpful errors if the parsing fails or is ambiguous.
* A method that takes one or more 3d objects and returns the cuttable-shape definitions, including tabs and kerf compensation. This needs to define the lines the laser cutter should trace. 
  * Internal cuts should be included as a separate set of lines to cut, so that the cutter can be instructed to cut those first. 
  * Engravings should be included as a separate set of lines to engrave, so that the cutter can be instructed to engrave those first.
* A method that takes a set of cuttable-shapes and returns a single SVG file with all of the pieces laid out in approximately a square - "simple cut layout".
* A method that takes a set of cuttable-shapes and a material size and returns multiple SVG files with the pieces laid out as efficiently as possible on pages of the specified material size.
* A method that takes a set of cuttable-shapes and a material size and determines the most efficient way to create multiples of the shapes, trying out up to a configurable multiple of copies.