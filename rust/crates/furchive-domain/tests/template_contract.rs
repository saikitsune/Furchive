use furchive_domain::{FilenameTemplate, TemplateValues};

#[test]
fn untrusted_values_cannot_create_paths_or_recursive_tokens() {
    let template = FilenameTemplate::parse(
        "{source}/{artist}/{id}/{safeTitle}/{pool_name}/{page_number}.{ext}",
    )
    .unwrap();
    let values = TemplateValues {
        source: "../root",
        artist: "CON",
        id: "x/{artist}",
        safe_title: "a:b?\0",
        pool_name: "..",
        page_number: "\\\\host",
        ext: "png",
    };
    let result = template.render(&values).unwrap();
    assert_eq!(result, ".._root/_CON/x__artist_/a_b__/_/__host.png");
    assert_eq!(result.split('/').count(), 6);
    assert!(!result.contains('{'));
}

#[test]
fn reserved_names_empty_values_unicode_and_lengths_are_portable() {
    let template = FilenameTemplate::parse("{artist}.{ext}").unwrap();
    for name in [
        "CON", "nul", "PRN", "AUX", "COM1", "lpt9", "COM¹", "LPT²", "CONIN$", "CONOUT$",
    ] {
        assert_eq!(
            template
                .render(&TemplateValues {
                    artist: name,
                    ext: "png",
                    ..Default::default()
                })
                .unwrap(),
            format!("_{name}.png")
        );
    }
    assert_eq!(
        FilenameTemplate::parse("CON.txt")
            .unwrap()
            .render(&TemplateValues::default())
            .unwrap(),
        "_CON.txt"
    );
    assert_eq!(
        FilenameTemplate::parse("{artist}")
            .unwrap()
            .render(&TemplateValues::default())
            .unwrap(),
        "_"
    );
    assert_eq!(
        FilenameTemplate::parse("{artist}")
            .unwrap()
            .render(&TemplateValues {
                artist: "水獺. ",
                ..Default::default()
            })
            .unwrap(),
        "水獺"
    );
    assert!(template
        .render(&TemplateValues {
            artist: &"x".repeat(256),
            ext: "png",
            ..Default::default()
        })
        .is_err());
}

#[test]
fn rejects_roots_traversal_unknown_tokens_and_malformed_syntax() {
    for invalid in [
        "",
        "/etc/{id}",
        "\\\\server\\share",
        "C:relative",
        "C:/absolute",
        "a/../b",
        "a\\..\\b",
        "./x",
        "a//b",
        "a/",
        "{title}",
        "{safetitle}",
        "{safe_title}",
        "{id",
        "id}",
        "{{id}}",
        "a\0b",
        "bad:name",
        "foo./bar",
    ] {
        assert!(
            FilenameTemplate::parse(invalid).is_err(),
            "accepted {invalid:?}"
        );
    }
    assert!(FilenameTemplate::parse("{source}\\{id}.{ext}").is_ok());
}

#[test]
fn exact_tokens_expand_with_pool_page_context() {
    let template = FilenameTemplate::parse(
        "{source}/{artist}/{pool_name}/{page_number}_{id}_{safeTitle}.{ext}",
    )
    .unwrap();
    let values = TemplateValues {
        source: "e621",
        artist: "river otter",
        id: "42",
        safe_title: "A title",
        pool_name: "test pool",
        page_number: "00002",
        ext: "png",
    };
    assert_eq!(
        template.render(&values).unwrap(),
        "e621/river_otter/test_pool/00002_42_A_title.png"
    );
}
